using InnerShiftLab.Core;
using Xunit;

namespace InnerShiftLab.Tests;

public class SqliteRepositoryTests : IAsyncLifetime
{
    private string _dir = "";
    private SqliteRepository _repo = null!;

    public async Task InitializeAsync()
    {
        _dir = Directory.CreateTempSubdirectory("iris_db_").FullName;
        _repo = new SqliteRepository($"Data Source={Path.Combine(_dir, "iris.db")}");
        await _repo.InitAsync();
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Ping_OnInitializedDb_ReturnsTrue()
    {
        Assert.True(await _repo.PingAsync());
    }

    [Fact]
    public async Task SavePost_ThenGetPending_ReturnsQueuedSlot()
    {
        var slot = new PostSlot
        {
            HookId = "hookA",
            Pillar = Pillar.Stabilise,
            Platforms = new[] { "instagram", "facebook" },
            Caption = "hello",
            Status = PostStatus.Queued,
            ScheduledAt = DateTimeOffset.UtcNow,
        };
        await _repo.SavePostAsync(slot);

        var pending = await _repo.GetPendingPostsAsync();
        var got = Assert.Single(pending);
        Assert.Equal("hookA", got.HookId);
        Assert.Equal(Pillar.Stabilise, got.Pillar);
        Assert.Equal(new[] { "instagram", "facebook" }, got.Platforms);
    }

    [Fact]
    public async Task SavePost_Upsert_UpdatesStatusNotDuplicates()
    {
        var slot = new PostSlot { HookId = "h", Status = PostStatus.Queued, ScheduledAt = DateTimeOffset.UtcNow };
        await _repo.SavePostAsync(slot);
        slot.Status = PostStatus.Published;
        await _repo.SavePostAsync(slot);

        Assert.Empty(await _repo.GetPendingPostsAsync()); // no longer Queued/Publishing
    }

    [Fact]
    public async Task SaveConversion_ThenGet_ReturnsNewestFirst()
    {
        await _repo.SaveConversionAsync(new Conversion { PostId = "p1", EventType = "click", UtmCampaign = "c1" });
        await _repo.SaveConversionAsync(new Conversion { PostId = "p2", EventType = "skool_join", UtmCampaign = "c2", RevenueEur = 37m });

        var list = await _repo.GetConversionsAsync(10);
        Assert.Equal(2, list.Count);
        Assert.Equal("p2", list[0].PostId);      // newest first
        Assert.Equal(37m, list[0].RevenueEur);
        Assert.Null(list[1].RevenueEur);
    }

    [Fact]
    public async Task SaveTokens_ThenLoad_RoundTrips()
    {
        await _repo.SaveTokensAsync("meta", "{\"a\":1}");
        Assert.Equal("{\"a\":1}", await _repo.LoadTokensAsync("meta"));

        // Upsert overwrites.
        await _repo.SaveTokensAsync("meta", "{\"a\":2}");
        Assert.Equal("{\"a\":2}", await _repo.LoadTokensAsync("meta"));
    }

    [Fact]
    public async Task GetConversions_RespectsLimit()
    {
        for (var i = 0; i < 5; i++)
            await _repo.SaveConversionAsync(new Conversion { PostId = $"p{i}", EventType = "click" });

        Assert.Equal(3, (await _repo.GetConversionsAsync(3)).Count);
    }

    private static OutboxItem NewOutboxItem(string packageId, string platform) => new()
    {
        PackageId = packageId,
        Platform = platform,
        HookId = "hook-01",
        Pillar = Pillar.Reprogram,
        Caption = "caption with utm link",
        Title = platform == "youtube" ? "A title" : "",
        MediaPath = $"/output/{packageId}/{platform}.png",
        Width = 1080,
        Height = 1350,
    };

    [Fact]
    public async Task SaveOutboxItem_ThenGetPackage_RoundTrips()
    {
        await _repo.SaveOutboxItemAsync(NewOutboxItem("pkg1", "instagram"));
        await _repo.SaveOutboxItemAsync(NewOutboxItem("pkg1", "youtube"));

        var pkg = await _repo.GetOutboxPackageAsync("pkg1");
        Assert.Equal(2, pkg.Count);
        var ig = Assert.Single(pkg, i => i.Platform == "instagram");
        Assert.Equal("hook-01", ig.HookId);
        Assert.Equal(Pillar.Reprogram, ig.Pillar);
        Assert.Equal("caption with utm link", ig.Caption);
        Assert.Equal(1080, ig.Width);
        Assert.Equal(1350, ig.Height);
        Assert.Equal(OutboxStatus.Pending, ig.Status);
        Assert.Null(ig.PostedAt);
        Assert.Equal("A title", Assert.Single(pkg, i => i.Platform == "youtube").Title);
    }

    [Fact]
    public async Task SaveOutboxItem_Upserts_OnPackageAndPlatform()
    {
        var item = NewOutboxItem("pkg1", "instagram");
        await _repo.SaveOutboxItemAsync(item);
        item.Caption = "updated";
        await _repo.SaveOutboxItemAsync(item);

        var pkg = await _repo.GetOutboxPackageAsync("pkg1");
        Assert.Equal("updated", Assert.Single(pkg).Caption);
    }

    [Fact]
    public async Task GetOutboxItems_FiltersByStatus()
    {
        await _repo.SaveOutboxItemAsync(NewOutboxItem("pkg1", "instagram"));
        await _repo.SaveOutboxItemAsync(NewOutboxItem("pkg1", "tiktok"));
        await _repo.MarkOutboxPostedAsync("pkg1", "tiktok", "https://tiktok.com/x");

        Assert.Single(await _repo.GetOutboxItemsAsync(OutboxStatus.Pending));
        Assert.Single(await _repo.GetOutboxItemsAsync(OutboxStatus.Posted));
        Assert.Equal(2, (await _repo.GetOutboxItemsAsync()).Count);
    }

    [Fact]
    public async Task MarkOutboxExported_OnlyPromotesPendingItems()
    {
        await _repo.SaveOutboxItemAsync(NewOutboxItem("pkg1", "instagram"));
        await _repo.SaveOutboxItemAsync(NewOutboxItem("pkg1", "tiktok"));
        await _repo.MarkOutboxPostedAsync("pkg1", "tiktok", null);

        var promoted = await _repo.MarkOutboxExportedAsync("pkg1", "drive://folder/abc");
        Assert.Equal(1, promoted);

        var pkg = await _repo.GetOutboxPackageAsync("pkg1");
        var ig = Assert.Single(pkg, i => i.Platform == "instagram");
        Assert.Equal(OutboxStatus.Exported, ig.Status);
        Assert.Equal("drive://folder/abc", ig.ExportRef);
        // The already-posted variant keeps its terminal status.
        Assert.Equal(OutboxStatus.Posted, Assert.Single(pkg, i => i.Platform == "tiktok").Status);
    }

    [Fact]
    public async Task MarkOutboxPosted_SetsUrlAndTimestamp_AndReportsMissing()
    {
        await _repo.SaveOutboxItemAsync(NewOutboxItem("pkg1", "instagram"));

        Assert.True(await _repo.MarkOutboxPostedAsync("pkg1", "instagram", "https://instagram.com/p/1"));
        Assert.False(await _repo.MarkOutboxPostedAsync("pkg1", "facebook", null));

        var ig = Assert.Single(await _repo.GetOutboxPackageAsync("pkg1"));
        Assert.Equal(OutboxStatus.Posted, ig.Status);
        Assert.Equal("https://instagram.com/p/1", ig.PostUrl);
        Assert.NotNull(ig.PostedAt);
    }
}
