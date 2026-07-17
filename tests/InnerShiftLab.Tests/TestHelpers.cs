using InnerShiftLab.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace InnerShiftLab.Tests;

/// <summary>Minimal IWebHostEnvironment for constructing services under test.</summary>
public sealed class TestEnv : IWebHostEnvironment
{
    public TestEnv(string contentRoot) => ContentRootPath = contentRoot;
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "InnerShiftLab.Tests";
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>In-memory IRepository so logic can be tested without a real database.</summary>
public sealed class FakeRepository : IRepository
{
    public readonly List<PostSlot> Posts = new();
    public readonly List<Conversion> Conversions = new();
    public readonly List<(string Source, string Body)> Webhooks = new();
    public readonly Dictionary<string, string> Tokens = new();

    public Task InitAsync() => Task.CompletedTask;
    public Task<bool> PingAsync() => Task.FromResult(true);

    public Task SaveTokensAsync(string provider, string json) { Tokens[provider] = json; return Task.CompletedTask; }
    public Task<string?> LoadTokensAsync(string provider) => Task.FromResult(Tokens.TryGetValue(provider, out var v) ? v : null);

    public Task SavePostAsync(PostSlot slot)
    {
        Posts.RemoveAll(p => p.SlotId == slot.SlotId);
        Posts.Add(slot);
        return Task.CompletedTask;
    }

    public Task<PostSlot?> GetPostAsync(string slotId)
        => Task.FromResult(Posts.FirstOrDefault(p => p.SlotId == slotId));

    public Task<IReadOnlyList<PostSlot>> GetPendingPostsAsync()
        => Task.FromResult<IReadOnlyList<PostSlot>>(
            Posts.Where(p => p.Status is PostStatus.Queued or PostStatus.Publishing).ToList());

    public Task<IReadOnlyList<PostSlot>> GetPublishedTodayAsync()
        => Task.FromResult<IReadOnlyList<PostSlot>>(
            Posts.Where(p => p.Status == PostStatus.Published
                && p.ScheduledAt >= DateTimeOffset.UtcNow.Date).ToList());

    public Task RecordWebhookAsync(string source, string body) { Webhooks.Add((source, body)); return Task.CompletedTask; }

    public Task SaveConversionAsync(Conversion c) { Conversions.Add(c); return Task.CompletedTask; }

    public Task<IReadOnlyList<Conversion>> GetConversionsAsync(int limit)
        => Task.FromResult<IReadOnlyList<Conversion>>(
            Conversions.AsEnumerable().Reverse().Take(limit).ToList());

    public readonly List<OutboxItem> Outbox = new();

    public Task SaveOutboxItemAsync(OutboxItem item)
    {
        // Mirror the SQLite upsert guard: terminal states never regress.
        var existing = Outbox.FirstOrDefault(o => o.PackageId == item.PackageId && o.Platform == item.Platform);
        if (existing != null && existing.Status is OutboxStatus.Posted or OutboxStatus.Skipped)
        {
            item.Status = existing.Status;
            item.PostedAt ??= existing.PostedAt;
            item.PostUrl ??= existing.PostUrl;
        }
        Outbox.RemoveAll(o => o.PackageId == item.PackageId && o.Platform == item.Platform);
        Outbox.Add(item);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxItem>> GetOutboxItemsAsync(OutboxStatus? status = null, int limit = 100)
        => Task.FromResult<IReadOnlyList<OutboxItem>>(
            Outbox.Where(o => status == null || o.Status == status).Take(limit).ToList());

    public Task<IReadOnlyList<OutboxItem>> GetOutboxPackageAsync(string packageId)
        => Task.FromResult<IReadOnlyList<OutboxItem>>(
            Outbox.Where(o => o.PackageId == packageId).ToList());

    public Task<IReadOnlyList<string>> GetUnexportedPackageIdsAsync(int limit = 20)
        => Task.FromResult<IReadOnlyList<string>>(
            Outbox.Where(o => o.Status == OutboxStatus.Pending)
                .OrderBy(o => o.CreatedAt)
                .Select(o => o.PackageId)
                .Distinct()
                .Take(limit)
                .ToList());

    public Task<int> MarkOutboxExportedAsync(string packageId, string exportRef)
    {
        var pending = Outbox.Where(o => o.PackageId == packageId && o.Status == OutboxStatus.Pending).ToList();
        foreach (var o in pending) { o.Status = OutboxStatus.Exported; o.ExportRef = exportRef; }
        return Task.FromResult(pending.Count);
    }

    public Task<bool> MarkOutboxPostedAsync(string packageId, string platform, string? postUrl)
    {
        var item = Outbox.FirstOrDefault(o => o.PackageId == packageId && o.Platform == platform);
        if (item == null || item.Status == OutboxStatus.Skipped) return Task.FromResult(false);
        item.Status = OutboxStatus.Posted;
        item.PostedAt = DateTimeOffset.UtcNow;
        item.PostUrl = postUrl;
        return Task.FromResult(true);
    }

    public Task<bool> MarkOutboxSkippedAsync(string packageId, string platform)
    {
        var item = Outbox.FirstOrDefault(o => o.PackageId == packageId && o.Platform == platform);
        if (item == null || item.Status == OutboxStatus.Posted) return Task.FromResult(false);
        item.Status = OutboxStatus.Skipped;
        return Task.FromResult(true);
    }

    public Task<int> MarkOutboxMediaPrunedAsync(string packageId)
    {
        var items = Outbox.Where(o => o.PackageId == packageId).ToList();
        foreach (var o in items) o.MediaPruned = true;
        return Task.FromResult(items.Count);
    }

    public Task<IReadOnlyList<string>> GetPrunablePackageIdsAsync(bool keepUnposted, DateTimeOffset olderThanUtc, int limit = 500)
    {
        IReadOnlyList<string> ids = Outbox
            .GroupBy(o => o.PackageId)
            .Where(g => g.All(o => !o.MediaPruned) && (
                g.All(o => o.Status is OutboxStatus.Posted or OutboxStatus.Skipped)
                || !keepUnposted
                || g.Min(o => o.CreatedAt) < olderThanUtc))
            .OrderBy(g => g.Min(o => o.CreatedAt))
            .Select(g => g.Key)
            .Take(limit)
            .ToList();
        return Task.FromResult(ids);
    }
}
