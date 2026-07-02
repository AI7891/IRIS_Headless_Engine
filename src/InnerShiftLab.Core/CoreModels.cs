// =============================================================================
//  Core models — entities, settings, contracts
// =============================================================================
namespace InnerShiftLab.Core;

public enum Pillar { Identify, Reprogram, Integrate, Stabilise }

public enum PostStatus { Draft, Queued, Publishing, Published, Failed }

public sealed class IrisSettings
{
    public string LinktreeUrl { get; set; } = "https://linktr.ee/dennis.p.santillan87";
    public string SkoolUrl { get; set; } = "https://www.skool.com/the-nervous-system-dojo-5604/about?ref=d2870f8972d8430690851c4b5d16730c";
    public string[] PostingTimesUtc { get; set; } = new[] { "09:00", "14:00", "19:00" };
    public int MaxPostsPerDayPerPlatform { get; set; } = 2;
    public string HeartbeatSecret { get; set; } = "change-me-in-env";
}

public sealed class SocialsSettings
{
    public InstagramSettings Instagram { get; set; } = new();
    public FacebookSettings  Facebook  { get; set; } = new();
    public TiktokSettings    Tiktok    { get; set; } = new();
    public YoutubeSettings   Youtube   { get; set; } = new();
}

public sealed class InstagramSettings
{
    public string Handle { get; set; } = "@the_inner_shift_lab";
    public string AppId { get; set; } = "";
    public string AppSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public long? BusinessAccountId { get; set; }
}

public sealed class FacebookSettings
{
    public string PageId { get; set; } = "";
    public string PageAccessToken { get; set; } = "";
}

public sealed class TiktokSettings
{
    public string Handle { get; set; } = "@theinnershiftlab";
    public string ClientKey { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";
}

public sealed class YoutubeSettings
{
    public string Handle { get; set; } = "@The_Inner_Shift_Lab";
    public string ChannelId { get; set; } = "UCHfWxzYcqzApXyfnm2A0JzQ";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";
}

public sealed class MonetizationSettings
{
    public decimal InnerCirclePriceEur { get; set; } = 37m;
    public decimal RootWorkLabPriceEur { get; set; } = 197m;
    public decimal StarterKitPriceEur { get; set; } = 27m;
    public string  SkoolWebhookSecret { get; set; } = "change-me";
    public string  MetaAppSecret { get; set; } = "change-me";
}

public sealed class Hook
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";   // pattern-interrupt, science, identity, method, community, proof
    public string Text { get; set; } = "";
    public Pillar PrimaryPillar { get; set; }
    public Pillar[] SecondaryPillars { get; set; } = Array.Empty<Pillar>();
    public string[] BestFor { get; set; } = Array.Empty<string>();  // platforms
    public int Score { get; set; } = 50;  // 0-100, higher = proven to convert
}

public sealed class PostSlot
{
    public string SlotId { get; set; } = Guid.NewGuid().ToString("N");
    public string HookId { get; set; } = "";
    public string HookText { get; set; } = "";
    public Pillar Pillar { get; set; }
    public string[] Platforms { get; set; } = Array.Empty<string>();
    public string Caption { get; set; } = "";
    public string? MediaUrl { get; set; }
    public DateTimeOffset ScheduledAt { get; set; }
    public PostStatus Status { get; set; } = PostStatus.Queued;
    public string[] PerPlatformPostIds { get; set; } = Array.Empty<string>();
    public string[] PerPlatformUrls { get; set; } = Array.Empty<string>();
    public string? Error { get; set; }
}

public sealed class TokenSet
{
    public string AccessToken { get; set; } = "";
    public string TokenType { get; set; } = "Bearer";
    public string? RefreshToken { get; set; }
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddHours(1);
    public string? PageAccessToken { get; set; }
    public string? PageId { get; set; }
    public string? IgBusinessId { get; set; }
    public string? IgUsername { get; set; }
}

public sealed class Conversion
{
    public string PostId { get; set; } = "";
    public string Platform { get; set; } = "";
    public string UtmCampaign { get; set; } = "";
    public string UtmContent { get; set; } = "";
    public string EventType { get; set; } = "";   // click, skool_join, inner_circle_join, root_work_lab_join
    public decimal? RevenueEur { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
