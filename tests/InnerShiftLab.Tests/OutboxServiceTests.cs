using InnerShiftLab.Core;
using InnerShiftLab.Engine;
using InnerShiftLab.Outbox;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InnerShiftLab.Tests;

public class OutboxServiceTests : IDisposable
{
    private readonly string _root;
    private readonly FakeRepository _repo = new();
    private readonly FakeEngine _engine = new();
    private readonly FakeBuilder _builder;
    private readonly FakeExporter _exporter = new();
    private readonly IrisSettings _irisSettings = new() { MaxPostsPerDayPerPlatform = 2 };
    private readonly OutboxSettings _outboxSettings = new() { Platforms = new[] { "instagram", "tiktok" } };

    public OutboxServiceTests()
    {
        _root = Directory.CreateTempSubdirectory("iris_outbox_svc_").FullName;
        _builder = new FakeBuilder(_repo, _root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private OutboxService Service()
        => new(_engine, _builder, _exporter, _repo, _irisSettings, _outboxSettings,
            NullLogger<OutboxService>.Instance);

    [Fact]
    public async Task BuildDaily_EmptyQueue_AutoCuratesAndDrainsQueueWithinCaps()
    {
        var packages = await Service().BuildDailyPackagesAsync();

        // Two hooks auto-curated; cap is 2/platform and each package uses 1 per platform,
        // so both get packaged, best-scored hook first.
        Assert.Equal(2, packages.Count);
        Assert.Equal("hook-high", packages[0].HookId);
        Assert.Equal("hook-low", packages[1].HookId);
        Assert.Empty(_engine.GetCurrentQueue());

        foreach (var package in packages)
        {
            var items = await _repo.GetOutboxPackageAsync(package.PackageId);
            Assert.All(items, i => Assert.Equal(OutboxStatus.Exported, i.Status));
            Assert.All(items, i => Assert.Equal("drive://exported", i.ExportRef));
        }
    }

    [Fact]
    public async Task BuildDaily_HonorsMaxPostsPerDayPerPlatform()
    {
        _irisSettings.MaxPostsPerDayPerPlatform = 1;

        var packages = await Service().BuildDailyPackagesAsync();

        // One package exhausts the cap on both platforms; the second slot stays queued.
        var package = Assert.Single(packages);
        Assert.Equal("hook-high", package.HookId);
        var remaining = Assert.Single(_engine.GetCurrentQueue());
        Assert.Equal("hook-low", remaining.HookId);
    }

    [Fact]
    public async Task BuildDaily_CapSurvivesRestart_SeededFromRepository()
    {
        _irisSettings.MaxPostsPerDayPerPlatform = 1;
        // Simulate an earlier run today (before a container restart): items already in SQLite.
        await _repo.SaveOutboxItemAsync(new OutboxItem
        {
            PackageId = "earlier", Platform = "instagram", MediaPath = "/x/instagram/media.png",
            Status = OutboxStatus.Exported, ExportRef = "drive://earlier",
        });
        await _repo.SaveOutboxItemAsync(new OutboxItem
        {
            PackageId = "earlier", Platform = "tiktok", MediaPath = "/x/tiktok/media.png",
            Status = OutboxStatus.Exported, ExportRef = "drive://earlier",
        });

        var packages = await Service().BuildDailyPackagesAsync();

        Assert.Empty(packages);
        // Curated slots stay queued instead of blowing past the cap.
        Assert.Equal(2, _engine.GetCurrentQueue().Count);
    }

    [Fact]
    public async Task BuildDaily_TracksSlotInPostsTable_AsQueued()
    {
        var packages = await Service().BuildDailyPackagesAsync();

        foreach (var package in packages)
        {
            var post = Assert.Single(_repo.Posts, p => p.SlotId == package.PackageId);
            Assert.Equal(PostStatus.Queued, post.Status);
        }
    }

    [Fact]
    public async Task BuildDaily_NoHooksAtAll_ReturnsEmpty()
    {
        _engine.Hooks.Clear();

        Assert.Empty(await Service().BuildDailyPackagesAsync());
    }

    [Fact]
    public async Task BuildDaily_ExportFailure_LeavesItemsPendingButReturnsPackage()
    {
        _exporter.Fail = true;

        var packages = await Service().BuildDailyPackagesAsync();

        Assert.NotEmpty(packages);
        var items = await _repo.GetOutboxPackageAsync(packages[0].PackageId);
        Assert.All(items, i => Assert.Equal(OutboxStatus.Pending, i.Status));
    }

    [Fact]
    public async Task RetryExport_ExportsPreviouslyFailedPackage()
    {
        _exporter.Fail = true;
        var packages = await Service().BuildDailyPackagesAsync();
        var id = packages[0].PackageId;

        _exporter.Fail = false;
        var exportRef = await Service().RetryExportAsync(id);

        Assert.Equal("drive://exported", exportRef);
        var items = await _repo.GetOutboxPackageAsync(id);
        Assert.All(items, i => Assert.Equal(OutboxStatus.Exported, i.Status));
    }

    [Fact]
    public async Task RetryExport_AlreadyExported_IsIdempotent()
    {
        var packages = await Service().BuildDailyPackagesAsync();
        var callsAfterBuild = _exporter.Calls;

        var exportRef = await Service().RetryExportAsync(packages[0].PackageId);

        Assert.Equal("drive://exported", exportRef);
        Assert.Equal(callsAfterBuild, _exporter.Calls); // no extra export happened
    }

    [Fact]
    public async Task RetryExport_FilesGoneFromDisk_ThrowsWithGuidance()
    {
        _exporter.Fail = true;
        var packages = await Service().BuildDailyPackagesAsync();
        Directory.Delete(packages[0].PackageDir, recursive: true);

        _exporter.Fail = false;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service().RetryExportAsync(packages[0].PackageId));
        Assert.Contains("/api/outbox/build", ex.Message);
    }

    [Fact]
    public async Task RetryExport_UnknownPackage_ReturnsNull()
    {
        Assert.Null(await Service().RetryExportAsync("nope"));
    }

    [Fact]
    public async Task BuildDaily_RetriesPendingExportsFromEarlierRuns()
    {
        _exporter.Fail = true;
        var failedPackages = await Service().BuildDailyPackagesAsync();
        var failedId = failedPackages[0].PackageId;

        // Next daily run: Drive is back.
        _exporter.Fail = false;
        _irisSettings.MaxPostsPerDayPerPlatform = 100; // caps not under test here
        await Service().BuildDailyPackagesAsync();

        var items = await _repo.GetOutboxPackageAsync(failedId);
        Assert.All(items, i => Assert.Equal(OutboxStatus.Exported, i.Status));
        Assert.All(items, i => Assert.Equal("drive://exported", i.ExportRef));
    }

    [Fact]
    public async Task Confirm_UnknownVariant_ReturnsFalse()
    {
        Assert.False(await Service().ConfirmPostedAsync("nope", "instagram", null));
    }

    [Fact]
    public async Task Confirm_PreservesBuildTimePostFields()
    {
        var service = Service();
        var packages = await service.BuildDailyPackagesAsync();
        var id = packages[0].PackageId;
        var original = Assert.Single(_repo.Posts, p => p.SlotId == id);
        var hookText = original.HookText;
        var scheduledAt = original.ScheduledAt;

        await service.ConfirmPostedAsync(id, "instagram", "https://instagram.com/p/1");

        // The row saved at build time is updated in place, not reconstructed.
        var post = Assert.Single(_repo.Posts, p => p.SlotId == id);
        Assert.Equal(hookText, post.HookText);
        Assert.Equal(scheduledAt, post.ScheduledAt);
        Assert.Equal(PostStatus.Publishing, post.Status);
    }

    [Fact]
    public async Task Skip_ThenConfirmRest_PublishesWithOnlyPostedUrls()
    {
        var service = Service();
        var packages = await service.BuildDailyPackagesAsync();
        var id = packages[0].PackageId;

        Assert.True(await service.SkipAsync(id, "tiktok"));
        Assert.True(await service.ConfirmPostedAsync(id, "instagram", "https://instagram.com/p/1"));

        var post = Assert.Single(_repo.Posts, p => p.SlotId == id);
        Assert.Equal(PostStatus.Published, post.Status);
        Assert.Equal(new[] { "https://instagram.com/p/1" }, post.PerPlatformUrls);
        Assert.Equal(OutboxStatus.Skipped,
            Assert.Single(await _repo.GetOutboxPackageAsync(id), i => i.Platform == "tiktok").Status);
    }

    [Fact]
    public async Task Skip_All_MarksPostRowFailed()
    {
        var service = Service();
        var packages = await service.BuildDailyPackagesAsync();
        var id = packages[0].PackageId;

        Assert.True(await service.SkipAsync(id, "instagram"));
        Assert.True(await service.SkipAsync(id, "tiktok"));

        var post = Assert.Single(_repo.Posts, p => p.SlotId == id);
        Assert.Equal(PostStatus.Failed, post.Status);
        Assert.Contains("skipped", post.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SkippedVariant_CannotBeConfirmed_AndViceVersa()
    {
        var service = Service();
        var packages = await service.BuildDailyPackagesAsync();
        var id = packages[0].PackageId;

        Assert.True(await service.SkipAsync(id, "tiktok"));
        Assert.False(await service.ConfirmPostedAsync(id, "tiktok", "https://tiktok.com/v/1"));

        Assert.True(await service.ConfirmPostedAsync(id, "instagram", null));
        Assert.False(await service.SkipAsync(id, "instagram"));
    }

    [Fact]
    public async Task Confirm_PartialThenFull_FlipsPostRowPublishingThenPublished()
    {
        var service = Service();
        var packages = await service.BuildDailyPackagesAsync();
        var id = packages[0].PackageId;

        Assert.True(await service.ConfirmPostedAsync(id, "instagram", "https://instagram.com/p/1"));
        var post = Assert.Single(_repo.Posts, p => p.SlotId == id);
        Assert.Equal(PostStatus.Publishing, post.Status);

        Assert.True(await service.ConfirmPostedAsync(id, "tiktok", "https://tiktok.com/v/2"));
        post = Assert.Single(_repo.Posts, p => p.SlotId == id);
        Assert.Equal(PostStatus.Published, post.Status);
        Assert.Equal(2, post.PerPlatformUrls.Length);

        var items = await _repo.GetOutboxPackageAsync(id);
        Assert.All(items, i => Assert.Equal(OutboxStatus.Posted, i.Status));
        Assert.Equal("https://instagram.com/p/1",
            Assert.Single(items, i => i.Platform == "instagram").PostUrl);
    }

    // -- fakes ----------------------------------------------------------------

    private sealed class FakeEngine : IIrisEngine
    {
        public readonly List<Hook> Hooks = new()
        {
            new Hook { Id = "hook-low",  Text = "low",  Score = 10, PrimaryPillar = Pillar.Identify,  BestFor = new[] { "instagram" } },
            new Hook { Id = "hook-high", Text = "high", Score = 90, PrimaryPillar = Pillar.Reprogram, BestFor = new[] { "instagram", "tiktok" } },
        };
        private readonly List<PostSlot> _queue = new();

        public IReadOnlyList<Hook> GetAllHooks() => Hooks;
        public Hook? GetHook(string id) => Hooks.FirstOrDefault(h => h.Id == id);

        public PostSlot Enqueue(string hookId, Pillar pillar, string[] platforms)
        {
            var hook = GetHook(hookId) ?? throw new KeyNotFoundException(hookId);
            var slot = new PostSlot
            {
                HookId = hook.Id, HookText = hook.Text, Pillar = pillar,
                Platforms = platforms, Caption = $"caption for {hook.Id}",
                ScheduledAt = DateTimeOffset.UtcNow,
            };
            _queue.Add(slot);
            return slot;
        }

        public IReadOnlyList<PostSlot> GetCurrentQueue() => _queue.ToArray();
        public IReadOnlyList<PostSlot> GetCurrentQueueForPlatform(string platform)
            => _queue.Where(s => s.Platforms.Contains(platform)).ToArray();
        public void RemoveFromQueue(PostSlot slot) => _queue.RemoveAll(s => s.SlotId == slot.SlotId);
        public void MarkPublished(PostSlot slot) => slot.Status = PostStatus.Published;
        public void MarkFailed(PostSlot slot, string error) { slot.Status = PostStatus.Failed; slot.Error = error; }
        public HookScore ScoreHook(Hook hook, string platform, DateTimeOffset targetTime) => new();
    }

    /// <summary>Builds variants for the requested platforms with stub files on disk, persisting them like the real builder does.</summary>
    private sealed class FakeBuilder : IOutboxPackageBuilder
    {
        private readonly FakeRepository _repo;
        private readonly string _root;
        public FakeBuilder(FakeRepository repo, string root) { _repo = repo; _root = root; }

        public async Task<OutboxPackage> BuildAsync(PostSlot slot, IReadOnlyCollection<string>? platforms = null, CancellationToken ct = default)
        {
            var packageDir = Path.Combine(_root, slot.SlotId);
            var items = new List<OutboxItem>();
            foreach (var platform in platforms ?? new[] { "instagram", "tiktok" })
            {
                var mediaPath = Path.Combine(packageDir, platform, "media.png");
                Directory.CreateDirectory(Path.GetDirectoryName(mediaPath)!);
                File.WriteAllText(mediaPath, "img");
                var item = new OutboxItem
                {
                    PackageId = slot.SlotId, Platform = platform, HookId = slot.HookId,
                    Pillar = slot.Pillar, Caption = slot.Caption,
                    MediaPath = mediaPath,
                    Width = 1080, Height = 1350,
                };
                await _repo.SaveOutboxItemAsync(item);
                items.Add(item);
            }
            return new OutboxPackage(slot.SlotId, slot.HookId, slot.HookText, slot.Pillar,
                DateTimeOffset.UtcNow, packageDir, Path.Combine(packageDir, "manifest.json"), items);
        }
    }

    private sealed class FakeExporter : IPackageExporter
    {
        public bool Fail;
        public int Calls;
        public Task<string> ExportAsync(OutboxPackage package, CancellationToken ct = default)
        {
            Calls++;
            return Fail ? throw new InvalidOperationException("drive down") : Task.FromResult("drive://exported");
        }
    }
}
