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
}
