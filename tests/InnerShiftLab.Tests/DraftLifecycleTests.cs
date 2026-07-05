using InnerShiftLab.Core;
using Xunit;

namespace InnerShiftLab.Tests;

public class DraftLifecycleTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteDraftRepository _repo;

    public DraftLifecycleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "iris-draft-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _repo = new SqliteDraftRepository($"Data Source={Path.Combine(_dir, "test.db")}");
        _repo.InitAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private Task<PostDraft> NewDraft(string platform = "instagram") =>
        _repo.CreateAsync(new PostDraft
        {
            Platform = platform,
            Caption = "Test caption with #hashtags",
            MediaReference = "/output/creator/final/test.mp4",
            ScheduledFor = DateTimeOffset.UtcNow.AddHours(2),
        });

    [Fact]
    public async Task NewDrafts_StartAsPendingApproval()
    {
        var draft = await NewDraft();
        Assert.True(draft.Id > 0);
        var loaded = await _repo.GetAsync(draft.Id);
        Assert.NotNull(loaded);
        Assert.Equal(DraftStatus.PendingApproval, loaded!.Status);
        Assert.Null(loaded.ApprovedAt);
        Assert.Null(loaded.PublishedAt);
    }

    [Fact]
    public async Task ApprovalPath_RecordsApprovedAt_AndReachesReadyToPublish()
    {
        var draft = await NewDraft();
        Assert.True(await _repo.TransitionAsync(draft.Id, DraftStatus.PendingApproval, DraftStatus.Approved));
        Assert.True(await _repo.TransitionAsync(draft.Id, DraftStatus.Approved, DraftStatus.ReadyToPublish));

        var loaded = await _repo.GetAsync(draft.Id);
        Assert.Equal(DraftStatus.ReadyToPublish, loaded!.Status);
        Assert.NotNull(loaded.ApprovedAt);
    }

    [Fact]
    public async Task Rejection_IsTerminal()
    {
        var draft = await NewDraft();
        Assert.True(await _repo.TransitionAsync(draft.Id, DraftStatus.PendingApproval, DraftStatus.Rejected));
        // No legal transition out of Rejected.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repo.TransitionAsync(draft.Id, DraftStatus.Rejected, DraftStatus.ReadyToPublish));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repo.TransitionAsync(draft.Id, DraftStatus.Rejected, DraftStatus.Published));
    }

    [Fact]
    public async Task Draft_CanNeverJumpStraightToPublished()
    {
        var draft = await NewDraft();
        // PendingApproval -> Published is illegal in the state machine…
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repo.TransitionAsync(draft.Id, DraftStatus.PendingApproval, DraftStatus.Published));
        // …and even a caller lying about the source state affects zero rows,
        // because the UPDATE is guarded by WHERE Status = expected.
        Assert.False(await _repo.TransitionAsync(draft.Id, DraftStatus.ReadyToPublish, DraftStatus.Published));
        Assert.Equal(DraftStatus.PendingApproval, (await _repo.GetAsync(draft.Id))!.Status);
    }

    [Fact]
    public async Task Publish_RecordsPlatformPostIdAndPublishedAt()
    {
        var draft = await NewDraft();
        await _repo.TransitionAsync(draft.Id, DraftStatus.PendingApproval, DraftStatus.Approved);
        await _repo.TransitionAsync(draft.Id, DraftStatus.Approved, DraftStatus.ReadyToPublish);
        Assert.True(await _repo.TransitionAsync(draft.Id, DraftStatus.ReadyToPublish, DraftStatus.Published,
            platformPostId: "1789_456"));

        var loaded = await _repo.GetAsync(draft.Id);
        Assert.Equal(DraftStatus.Published, loaded!.Status);
        Assert.Equal("1789_456", loaded.PlatformPostId);
        Assert.NotNull(loaded.PublishedAt);
    }

    [Fact]
    public async Task FailedPublish_RecordsError_AndCanBeRetried()
    {
        var draft = await NewDraft();
        await _repo.TransitionAsync(draft.Id, DraftStatus.PendingApproval, DraftStatus.Approved);
        await _repo.TransitionAsync(draft.Id, DraftStatus.Approved, DraftStatus.ReadyToPublish);
        await _repo.TransitionAsync(draft.Id, DraftStatus.ReadyToPublish, DraftStatus.Failed,
            errorMessage: "expired token");

        var failed = await _repo.GetAsync(draft.Id);
        Assert.Equal(DraftStatus.Failed, failed!.Status);
        Assert.Equal("expired token", failed.ErrorMessage);

        // Retry re-enters the publish queue (approval already happened) and clears the error.
        Assert.True(await _repo.TransitionAsync(draft.Id, DraftStatus.Failed, DraftStatus.ReadyToPublish));
        var retried = await _repo.GetAsync(draft.Id);
        Assert.Equal(DraftStatus.ReadyToPublish, retried!.Status);
        Assert.Null(retried.ErrorMessage);
    }

    [Fact]
    public async Task ListByStatus_FiltersCorrectly()
    {
        var a = await NewDraft("instagram");
        var b = await NewDraft("tiktok");
        await _repo.TransitionAsync(b.Id, DraftStatus.PendingApproval, DraftStatus.Rejected);

        var pending = await _repo.ListAsync(DraftStatus.PendingApproval);
        Assert.Single(pending);
        Assert.Equal(a.Id, pending[0].Id);

        var all = await _repo.ListAsync(null);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task CountCreatedToday_CountsPerPlatform()
    {
        await NewDraft("instagram");
        await NewDraft("instagram");
        await NewDraft("youtube");
        Assert.Equal(2, await _repo.CountCreatedTodayAsync("instagram"));
        Assert.Equal(1, await _repo.CountCreatedTodayAsync("youtube"));
        Assert.Equal(0, await _repo.CountCreatedTodayAsync("tiktok"));
    }

    [Fact]
    public void StateMachine_MatchesSpecifiedFlow()
    {
        // PendingApproval -> (Approved | Rejected)
        Assert.True(DraftStateMachine.CanTransition(DraftStatus.PendingApproval, DraftStatus.Approved));
        Assert.True(DraftStateMachine.CanTransition(DraftStatus.PendingApproval, DraftStatus.Rejected));
        // Approved -> ReadyToPublish -> (Published | Failed)
        Assert.True(DraftStateMachine.CanTransition(DraftStatus.Approved, DraftStatus.ReadyToPublish));
        Assert.True(DraftStateMachine.CanTransition(DraftStatus.ReadyToPublish, DraftStatus.Published));
        Assert.True(DraftStateMachine.CanTransition(DraftStatus.ReadyToPublish, DraftStatus.Failed));
        // Never straight to Published without Approved + ReadyToPublish
        Assert.False(DraftStateMachine.CanTransition(DraftStatus.PendingApproval, DraftStatus.Published));
        Assert.False(DraftStateMachine.CanTransition(DraftStatus.PendingApproval, DraftStatus.ReadyToPublish));
        Assert.False(DraftStateMachine.CanTransition(DraftStatus.Approved, DraftStatus.Published));
    }
}

public class TokenVaultPolicyTests
{
    [Fact]
    public async Task Vault_RejectsNonBearerCredentials()
    {
        var dir = Path.Combine(Path.GetTempPath(), "iris-vault-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var vault = new InnerShiftLab.Auth.TokenVault(new TestEnv(dir));
            // Session cookies / scraped credentials are not storable — OAuth Bearer only.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                vault.SaveTokensAsync("meta", new TokenSet { AccessToken = "abc", TokenType = "SessionCookie" }));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                vault.SaveTokensAsync("meta", new TokenSet { AccessToken = "", TokenType = "Bearer" }));
            // A proper OAuth bearer token round-trips.
            await vault.SaveTokensAsync("meta", new TokenSet
            {
                AccessToken = "official-oauth-token",
                TokenType = "Bearer",
                Scopes = new[] { "pages_show_list", "pages_manage_posts", "instagram_basic", "instagram_content_publish" },
            });
            var loaded = await vault.LoadTokensAsync("meta");
            Assert.Equal("official-oauth-token", loaded!.AccessToken);
            Assert.Contains("instagram_content_publish", loaded.Scopes);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
