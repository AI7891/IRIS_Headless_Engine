using InnerShiftLab.Core;
using InnerShiftLab.Engine;
using InnerShiftLab.Outbox;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InnerShiftLab.Tests;

public class OutboxServiceTests
{
    private readonly FakeRepository _repo = new();
    private readonly FakeEngine _engine = new();
    private readonly FakeBuilder _builder;
    private readonly FakeExporter _exporter = new();

    public OutboxServiceTests() => _builder = new FakeBuilder(_repo);

    private OutboxService Service()
        => new(_engine, _builder, _exporter, _repo, NullLogger<OutboxService>.Instance);

    [Fact]
    public async Task BuildDaily_EmptyQueue_AutoCuratesTopHooksAndBuilds()
    {
        var package = await Service().BuildDailyPackageAsync();

        Assert.NotNull(package);
        // Top-scored hook wins the day's package.
        Assert.Equal("hook-high", package!.HookId);
        // Built variants got promoted to Exported with the export reference.
        var items = await _repo.GetOutboxPackageAsync(package.PackageId);
        Assert.All(items, i => Assert.Equal(OutboxStatus.Exported, i.Status));
        Assert.All(items, i => Assert.Equal("drive://exported", i.ExportRef));
        // The packaged slot left the queue; remaining curated slots wait for tomorrow.
        Assert.DoesNotContain(_engine.GetCurrentQueue(), s => s.SlotId == package.PackageId);
    }

    [Fact]
    public async Task BuildDaily_TracksSlotInPostsTable_AsQueued()
    {
        var package = await Service().BuildDailyPackageAsync();

        var post = Assert.Single(_repo.Posts, p => p.SlotId == package!.PackageId);
        Assert.Equal(PostStatus.Queued, post.Status);
    }

    [Fact]
    public async Task BuildDaily_NoHooksAtAll_ReturnsNull()
    {
        _engine.Hooks.Clear();

        Assert.Null(await Service().BuildDailyPackageAsync());
    }

    [Fact]
    public async Task BuildDaily_ExportFailure_LeavesItemsPendingButReturnsPackage()
    {
        _exporter.Fail = true;

        var package = await Service().BuildDailyPackageAsync();

        Assert.NotNull(package);
        var items = await _repo.GetOutboxPackageAsync(package!.PackageId);
        Assert.All(items, i => Assert.Equal(OutboxStatus.Pending, i.Status));
    }

    [Fact]
    public async Task Confirm_UnknownVariant_ReturnsFalse()
    {
        Assert.False(await Service().ConfirmPostedAsync("nope", "instagram", null));
    }

    [Fact]
    public async Task Confirm_PartialThenFull_FlipsPostRowPublishingThenPublished()
    {
        var service = Service();
        var package = await service.BuildDailyPackageAsync();
        var id = package!.PackageId;

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

    /// <summary>Builds two variants without touching the filesystem, persisting them like the real builder does.</summary>
    private sealed class FakeBuilder : IOutboxPackageBuilder
    {
        private readonly FakeRepository _repo;
        public FakeBuilder(FakeRepository repo) => _repo = repo;

        public async Task<OutboxPackage> BuildAsync(PostSlot slot, CancellationToken ct = default)
        {
            var items = new List<OutboxItem>();
            foreach (var platform in new[] { "instagram", "tiktok" })
            {
                var item = new OutboxItem
                {
                    PackageId = slot.SlotId, Platform = platform, HookId = slot.HookId,
                    Pillar = slot.Pillar, Caption = slot.Caption, MediaPath = $"/x/{platform}.png",
                    Width = 1080, Height = 1350,
                };
                await _repo.SaveOutboxItemAsync(item);
                items.Add(item);
            }
            return new OutboxPackage(slot.SlotId, slot.HookId, slot.HookText, slot.Pillar,
                DateTimeOffset.UtcNow, "/pkg", "/pkg/manifest.json", items);
        }
    }

    private sealed class FakeExporter : IPackageExporter
    {
        public bool Fail;
        public Task<string> ExportAsync(OutboxPackage package, CancellationToken ct = default)
            => Fail ? throw new InvalidOperationException("drive down") : Task.FromResult("drive://exported");
    }
}
