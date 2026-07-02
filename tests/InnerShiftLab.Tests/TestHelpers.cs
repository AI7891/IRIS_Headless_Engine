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
}
