// =============================================================================
//  Outbox settings — which platforms to package for, and where to export.
// =============================================================================
namespace InnerShiftLab.Outbox;

public sealed class OutboxSettings
{
    public static readonly string[] DefaultPlatforms = { "instagram", "facebook", "tiktok", "youtube" };

    /// <summary>
    /// Platforms that get a formatted variant in every daily package. Empty means
    /// all supported platforms. Deliberately NOT defaulted to a filled array: the
    /// configuration binder appends bound array elements to a non-empty default
    /// instead of replacing it, which would silently duplicate every platform.
    /// Consume via <see cref="EffectivePlatforms"/>.
    /// </summary>
    public string[] Platforms { get; set; } = Array.Empty<string>();

    /// <summary>The de-duplicated platform list to build for, falling back to all supported platforms.</summary>
    public string[] EffectivePlatforms => Platforms.Length == 0
        ? DefaultPlatforms
        : Platforms.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// How many packages a single daily run produces (one package = one hook across
    /// all its platform variants). Defaults to 1 — the explicit, documented
    /// replacement for the former accidental one-package-per-day behaviour.
    /// </summary>
    public int PackagesPerRun { get; set; } = 1;

    /// <summary>
    /// Render an mp4 for video-first platforms (TikTok/YouTube) when ffmpeg is
    /// available. Falls back to the still image if rendering fails.
    /// </summary>
    public bool RenderVideo { get; set; } = true;

    /// <summary>
    /// Feed the AI content pipeline (Anthropic script → Pexels carousel →
    /// ElevenLabs voiceover → composed video) into each package instead of plain
    /// text cards. Opt-in: it costs API credits and needs the ContentCreator keys.
    /// Any pipeline failure falls back to text-card rendering (never sinks a run).
    /// </summary>
    public bool UseContentCreator { get; set; }

    /// <summary>
    /// Push each package to a branch of this repo for phone pickup via the GitHub app.
    /// The default delivery mechanism: uses the ambient Codespaces git credentials, so no
    /// new secret is stored, and unlike a Drive service account it works on a personal
    /// Google/GitHub account at zero cost.
    /// </summary>
    public GitExportSettings Git { get; set; } = new();

    public GoogleDriveSettings GoogleDrive { get; set; } = new();

    /// <summary>FIFO media retention. Rows in the outbox table are always kept (they carry
    /// attribution); only the rendered media/text files are pruned, oldest first.</summary>
    public RetentionSettings Retention { get; set; } = new();
}

public sealed class RetentionSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Hard age cap. Media older than this is pruned even if never posted (abandoned).</summary>
    public int KeepDays { get; set; } = 30;
    /// <summary>FIFO size cap on the local outbox root. Oldest eligible packages are pruned until under it. 0 = no size cap.</summary>
    public int MaxTotalMegabytes { get; set; } = 2048;
    /// <summary>Keep media for packages not yet posted/skipped, until KeepDays forces the issue.</summary>
    public bool KeepUnpostedPackages { get; set; } = true;
}

public sealed class GitExportSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Orphan branch the packages are published to. Never merged; not part of the code history.</summary>
    public string Branch { get; set; } = "outbox";
    /// <summary>Days of packages kept on the branch. Older ones are dropped on the next export.</summary>
    public int RetentionDays { get; set; } = 14;
    /// <summary>
    /// owner/repo to push packages to. <b>Strongly recommended: a dedicated private repo</b>,
    /// e.g. <c>you/IRIS_Outbox</c>. Empty falls back to the source code repo (via
    /// GITHUB_REPOSITORY / the origin remote), which means every future clone and Codespace
    /// rebuild downloads the media accumulated on the outbox branch.
    /// </summary>
    public string Repository { get; set; } = "";
}

/// <summary>
/// Google Drive pickup folder. Authenticates with a service-account key —
/// deliberately NOT user OAuth, so no long-lived personal tokens are stored
/// (the compliance concern that motivated retiring automated posting).
/// </summary>
public sealed class GoogleDriveSettings
{
    public bool Enabled { get; set; }

    /// <summary>Path to the service-account JSON key. Falls back to the GOOGLE_APPLICATION_CREDENTIALS env var when empty.</summary>
    public string ServiceAccountJsonPath { get; set; } = "";

    /// <summary>Drive folder the operator watches from the phone. Must be shared with the service-account email.</summary>
    public string FolderId { get; set; } = "";
}
