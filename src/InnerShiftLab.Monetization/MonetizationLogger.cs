// =============================================================================
//  Monetization Logger — links UTMs to conversions + KPI summary
// =============================================================================
using InnerShiftLab.Core;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.Text.RegularExpressions;

namespace InnerShiftLab.Monetization;

public interface IMonetizationLogger
{
    Task LogPostAsync(PostSlot post);
    Task LogSkoolJoinAsync(string body);
    Task LogClickAsync(string postId, string platform, string utmCampaign, string utmContent);
    Task<MonetizationSummary> GetSummaryAsync();
    Task<IReadOnlyList<Conversion>> GetConversionsAsync(int limit = 100);
}

public sealed class MonetizationLogger : IMonetizationLogger
{
    private readonly IRepository _repo;
    private readonly MonetizationSettings _mon;
    private readonly ILogger<MonetizationLogger> _log;

    public MonetizationLogger(IRepository repo, IOptions<MonetizationSettings> mon, ILogger<MonetizationLogger> log)
    {
        _repo = repo; _mon = mon.Value; _log = log;
    }

    public async Task LogPostAsync(PostSlot post)
    {
        await _repo.SavePostAsync(post);
        _log.LogInformation("Post logged: {Hook} on {Platforms} status={Status}",
            post.HookId, string.Join(",", post.Platforms), post.Status);
    }

    public async Task LogClickAsync(string postId, string platform, string utmCampaign, string utmContent)
    {
        var c = new Conversion
        {
            PostId = postId, Platform = platform, UtmCampaign = utmCampaign, UtmContent = utmContent,
            EventType = "click", Timestamp = DateTimeOffset.UtcNow,
        };
        await _repo.SaveConversionAsync(c);
    }

    public async Task LogSkoolJoinAsync(string body)
    {
        SkoolJoinPayload? payload = null;
        try { payload = JsonConvert.DeserializeObject<SkoolJoinPayload>(body); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to parse Skool join payload"); return; }
        if (payload == null) return;

        decimal? revenue = payload.Plan switch
        {
            "inner_circle" => _mon.InnerCirclePriceEur,
            "root_work_lab" => _mon.RootWorkLabPriceEur,
            "starter_kit" => _mon.StarterKitPriceEur,
            _ => null,
        };

        var (camp, content, source) = ExtractUtms(payload.Ref ?? payload.Metadata);

        // Platform attribution: explicit payload field wins, then the utm_source the
        // outbox stamped per platform variant, then "skool" as the last resort.
        var platform = !string.IsNullOrEmpty(payload.Platform) ? payload.Platform
            : !string.IsNullOrEmpty(source) ? source
            : "skool";

        var c = new Conversion
        {
            PostId = payload.PostId ?? "",
            Platform = platform,
            UtmCampaign = camp,
            UtmContent = content,
            EventType = "skool_join",
            RevenueEur = revenue,
            Timestamp = DateTimeOffset.UtcNow,
        };
        await _repo.SaveConversionAsync(c);
        _log.LogInformation("Skool join: plan={Plan} ref={Ref} revenue={Rev}€",
            payload.Plan, payload.Ref, revenue);
    }

    public async Task<MonetizationSummary> GetSummaryAsync()
    {
        var all = await _repo.GetConversionsAsync(1000);
        var byCampaign = all
            .Where(c => !string.IsNullOrEmpty(c.UtmCampaign))
            .GroupBy(c => c.UtmCampaign)
            .Select(g => new CampaignStat
            {
                Campaign = g.Key,
                Joins = g.Count(c => c.EventType == "skool_join"),
                Clicks = g.Count(c => c.EventType == "click"),
                RevenueEur = g.Where(c => c.RevenueEur.HasValue).Sum(c => c.RevenueEur!.Value),
            })
            .OrderByDescending(s => s.RevenueEur)
            .ToList();
        var byPlatform = all
            .Where(c => !string.IsNullOrEmpty(c.Platform))
            .GroupBy(c => c.Platform, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PlatformStat
            {
                Platform = g.Key.ToLowerInvariant(),
                Joins = g.Count(c => c.EventType == "skool_join"),
                Clicks = g.Count(c => c.EventType == "click"),
                RevenueEur = g.Where(c => c.RevenueEur.HasValue).Sum(c => c.RevenueEur!.Value),
            })
            .OrderByDescending(s => s.RevenueEur)
            .ToList();
        return new MonetizationSummary
        {
            TotalJoins = all.Count(c => c.EventType == "skool_join"),
            TotalClicks = all.Count(c => c.EventType == "click"),
            TotalRevenueEur = all.Where(c => c.RevenueEur.HasValue).Sum(c => c.RevenueEur!.Value),
            ByCampaign = byCampaign,
            ByPlatform = byPlatform,
        };
    }

    public async Task<IReadOnlyList<Conversion>> GetConversionsAsync(int limit = 100)
        => await _repo.GetConversionsAsync(limit);

    private static (string Campaign, string Content, string Source) ExtractUtms(string? s)
    {
        if (string.IsNullOrEmpty(s)) return ("", "", "");
        var m = Regex.Match(s, "utm_campaign=([^&]+)");
        var m2 = Regex.Match(s, "utm_content=([^&]+)");
        var m3 = Regex.Match(s, "utm_source=([^&]+)");
        return (
            m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : "",
            m2.Success ? Uri.UnescapeDataString(m2.Groups[1].Value) : "",
            m3.Success ? Uri.UnescapeDataString(m3.Groups[1].Value) : ""
        );
    }

    private sealed class SkoolJoinPayload
    {
        [JsonProperty("plan")] public string? Plan { get; set; }
        [JsonProperty("ref")] public string? Ref { get; set; }
        [JsonProperty("post_id")] public string? PostId { get; set; }
        [JsonProperty("platform")] public string? Platform { get; set; }
        [JsonProperty("metadata")] public string? Metadata { get; set; }
    }
}

public sealed class MonetizationSummary
{
    public int TotalJoins { get; set; }
    public int TotalClicks { get; set; }
    public decimal TotalRevenueEur { get; set; }
    public List<CampaignStat> ByCampaign { get; set; } = new();
    public List<PlatformStat> ByPlatform { get; set; } = new();
}

public sealed class PlatformStat
{
    public string Platform { get; set; } = "";
    public int Joins { get; set; }
    public int Clicks { get; set; }
    public decimal RevenueEur { get; set; }
}

public sealed class CampaignStat
{
    public string Campaign { get; set; } = "";
    public int Joins { get; set; }
    public int Clicks { get; set; }
    public decimal RevenueEur { get; set; }
}

