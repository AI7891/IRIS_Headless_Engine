using InnerShiftLab.Core;
using InnerShiftLab.Outbox;
using Xunit;

namespace InnerShiftLab.Tests;

public class OutboxRetentionTests
{
    // -- FIFO size-pass helper -------------------------------------------------

    [Fact]
    public void SizePass_PrunesOldestFirst_AndStopsUnderCap()
    {
        // oldest-first: p1(500) p2(500) p3(500) p4(500), total 2000, cap 1200.
        var items = new List<(string, long)> { ("p1", 500), ("p2", 500), ("p3", 500), ("p4", 500) };
        var toPrune = OutboxRetention.SelectForSizePass(items, currentBytes: 2000, capBytes: 1200);

        // 2000 -> prune p1 -> 1500 (still over) -> prune p2 -> 1000 (<=1200) stop.
        Assert.Equal(new[] { "p1", "p2" }, toPrune);
    }

    [Fact]
    public void SizePass_AlreadyUnderCap_PrunesNothing()
    {
        var items = new List<(string, long)> { ("p1", 100), ("p2", 100) };
        Assert.Empty(OutboxRetention.SelectForSizePass(items, currentBytes: 200, capBytes: 500));
    }

    [Fact]
    public void SizePass_ZeroCap_MeansNoSizeCap()
    {
        var items = new List<(string, long)> { ("p1", 100) };
        Assert.Empty(OutboxRetention.SelectForSizePass(items, currentBytes: 100, capBytes: 0));
    }

    [Fact]
    public void SizePass_NeverOverPrunes_NewestSurvive()
    {
        var items = new List<(string, long)> { ("old", 900), ("mid", 900), ("new", 900) };
        var toPrune = OutboxRetention.SelectForSizePass(items, currentBytes: 2700, capBytes: 2000);
        Assert.Equal(new[] { "old" }, toPrune); // 2700-900=1800<=2000, stop; mid+new survive
    }

    // -- path guard ------------------------------------------------------------

    [Fact]
    public void IsInsideRoot_AcceptsChild_RejectsOutside()
    {
        var root = Path.Combine(Path.GetTempPath(), "iris-root");
        Assert.True(OutboxRetention.IsInsideRoot(root, Path.Combine(root, "2026-07-16", "pkg")));
        Assert.True(OutboxRetention.IsInsideRoot(root, root));
        Assert.False(OutboxRetention.IsInsideRoot(root, Path.Combine(root, "..", "elsewhere")));
        Assert.False(OutboxRetention.IsInsideRoot(root, "/etc/passwd"));
        // A sibling dir sharing a name prefix must not be considered inside.
        Assert.False(OutboxRetention.IsInsideRoot(root, root + "-evil"));
    }

    // -- Drive folder-id parsing ----------------------------------------------

    [Fact]
    public void ExtractFolderId_ParsesDriveUrl_NullForGitOrLocal()
    {
        Assert.Equal("ABC123",
            GoogleDrivePackageExporter.ExtractFolderId("https://drive.google.com/drive/folders/ABC123"));
        Assert.Null(GoogleDrivePackageExporter.ExtractFolderId(
            "https://github.com/o/r/tree/outbox/2026-07-16/pkg"));
        Assert.Null(GoogleDrivePackageExporter.ExtractFolderId("/home/user/output/outbox/2026-07-16/pkg"));
        Assert.Null(GoogleDrivePackageExporter.ExtractFolderId(""));
    }

    // -- repository prunable-selection + flag ----------------------------------

    private static OutboxItem Item(string pkg, string platform, OutboxStatus status, DateTimeOffset created) => new()
    {
        PackageId = pkg, Platform = platform, Status = status, CreatedAt = created,
        Caption = "cap", ExportRef = "https://github.com/o/r/tree/outbox/x", MediaPath = $"/x/{pkg}/{platform}.png",
    };

    [Fact]
    public async Task GetPrunable_OldestFirst_ExcludesUnpostedWithinKeep_IncludesWhenOld_ExcludesPruned()
    {
        var repo = new FakeRepository();
        var now = DateTimeOffset.UtcNow;
        // p-unposted-old: has a Pending item but oldest -> prunable (abandoned), first
        await repo.SaveOutboxItemAsync(Item("p-unposted-old", "instagram", OutboxStatus.Pending, now.AddDays(-45)));
        // p-old: posted, older than cutoff -> prunable, second
        await repo.SaveOutboxItemAsync(Item("p-old", "instagram", OutboxStatus.Posted, now.AddDays(-40)));
        // p-unposted-recent: has a Pending item, recent -> NOT prunable when keepUnposted
        await repo.SaveOutboxItemAsync(Item("p-unposted-recent", "instagram", OutboxStatus.Pending, now.AddDays(-1)));
        // p-pruned: posted but already pruned -> excluded
        await repo.SaveOutboxItemAsync(Item("p-pruned", "instagram", OutboxStatus.Posted, now.AddDays(-50)));
        await repo.MarkOutboxMediaPrunedAsync("p-pruned");

        var cutoff = now.AddDays(-30);
        var ids = await repo.GetPrunablePackageIdsAsync(keepUnposted: true, olderThanUtc: cutoff);

        Assert.Equal(new[] { "p-unposted-old", "p-old" }, ids); // oldest first; recent + pruned excluded
        Assert.DoesNotContain("p-unposted-recent", ids);
        Assert.DoesNotContain("p-pruned", ids);
    }

    [Fact]
    public async Task GetPrunable_KeepUnpostedFalse_IncludesRecentUnposted()
    {
        var repo = new FakeRepository();
        var now = DateTimeOffset.UtcNow;
        await repo.SaveOutboxItemAsync(Item("p", "instagram", OutboxStatus.Pending, now.AddDays(-1)));

        Assert.Contains("p", await repo.GetPrunablePackageIdsAsync(keepUnposted: false, olderThanUtc: now.AddDays(-30)));
    }

    [Fact]
    public async Task MarkMediaPruned_SetsFlag_KeepsCaptionExportRefStatus()
    {
        var repo = new FakeRepository();
        await repo.SaveOutboxItemAsync(Item("p", "instagram", OutboxStatus.Posted, DateTimeOffset.UtcNow));

        var n = await repo.MarkOutboxMediaPrunedAsync("p");

        Assert.Equal(1, n);
        var item = Assert.Single(await repo.GetOutboxPackageAsync("p"));
        Assert.True(item.MediaPruned);
        Assert.Equal("cap", item.Caption);
        Assert.Equal("https://github.com/o/r/tree/outbox/x", item.ExportRef);
        Assert.Equal(OutboxStatus.Posted, item.Status);
    }
}
