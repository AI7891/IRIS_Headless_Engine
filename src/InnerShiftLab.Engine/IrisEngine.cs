// =============================================================================
//  IRIS Engine — content scoring, queueing, time-slot selection
// =============================================================================
using InnerShiftLab.Core;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace InnerShiftLab.Engine;

public interface IIrisEngine
{
    IReadOnlyList<Hook> GetAllHooks();
    Hook? GetHook(string id);
    PostSlot Enqueue(string hookId, Pillar pillar, string[] platforms);
    IReadOnlyList<PostSlot> GetCurrentQueue();
    IReadOnlyList<PostSlot> GetCurrentQueueForPlatform(string platform);
    void RemoveFromQueue(PostSlot slot);
    void MarkPublished(PostSlot slot);
    void MarkFailed(PostSlot slot, string error);
    HookScore ScoreHook(Hook hook, string platform, DateTimeOffset targetTime);
}

public sealed class HookScore
{
    public string HookId { get; set; } = "";
    public string Platform { get; set; } = "";
    public double PillarFit { get; set; }        // 0-1
    public double PlatformFit { get; set; }     // 0-1
    public double TimeFit { get; set; }          // 0-1
    public double ConversionPotential { get; set; } // 0-1
    public double Composite { get; set; }        // 0-1
    public string Reason { get; set; } = "";
}

public sealed class IrisEngine : IIrisEngine
{
    private readonly IrisSettings _settings;
    private readonly ILogger<IrisEngine> _log;
    private readonly List<Hook> _hooks = new();
    private readonly Dictionary<string, PillarWeight> _pillarWeights = new();
    private readonly List<PostSlot> _queue = new();
    private readonly object _queueLock = new();

    public IrisEngine(IOptions<IrisSettings> opts, ILogger<IrisEngine> log, IWebHostEnvironment env)
    {
        _settings = opts.Value;
        _log = log;
        var root = AppPaths.ResolveRoot(env.ContentRootPath);
        LoadHooks(AppPaths.DataFile(root, "hooks.json"));
        LoadPillars(AppPaths.DataFile(root, "pillars.json"));
    }

    private void LoadHooks(string path)
    {
        if (!File.Exists(path))
        {
            _log.LogError("hooks.json not found at {Path}", path);
            throw new FileNotFoundException("hooks.json missing — pipeline cannot operate", path);
        }
        var json = File.ReadAllText(path);
        var data = JsonConvert.DeserializeObject<HooksFile>(json)
            ?? throw new InvalidDataException("hooks.json is empty or malformed");
        _hooks.Clear();
        _hooks.AddRange(data.Hooks);
        _log.LogInformation("Loaded {Count} hooks from {Path}", _hooks.Count, path);
    }

    private void LoadPillars(string path)
    {
        if (!File.Exists(path))
        {
            _log.LogWarning("pillars.json not found at {Path}, using defaults", path);
            return;
        }
        var json = File.ReadAllText(path);
        var data = JsonConvert.DeserializeObject<PillarsFile>(json);
        if (data?.Pillars == null) return;
        foreach (var p in data.Pillars)
            _pillarWeights[p.Pillar] = p;
    }

    public IReadOnlyList<Hook> GetAllHooks() => _hooks.AsReadOnly();
    public Hook? GetHook(string id) => _hooks.FirstOrDefault(h => h.Id == id);

    public PostSlot Enqueue(string hookId, Pillar pillar, string[] platforms)
    {
        var hook = GetHook(hookId)
            ?? throw new KeyNotFoundException($"Hook '{hookId}' not found in IRIS engine");

        // Schedule at next posting time today, or tomorrow if past
        var scheduled = NextPostingTime(DateTimeOffset.UtcNow);

        var caption = BuildCaption(hook, pillar);
        var slot = new PostSlot
        {
            HookId = hook.Id,
            HookText = hook.Text,
            Pillar = pillar,
            Platforms = platforms ?? Array.Empty<string>(),
            Caption = caption,
            ScheduledAt = scheduled,
        };
        lock (_queueLock) _queue.Add(slot);
        _log.LogInformation("Enqueued slot {Id} for hook {Hook} pillar {Pillar} platforms {Platforms} @ {When:o}",
            slot.SlotId, hook.Id, pillar, string.Join(",", platforms ?? Array.Empty<string>()), scheduled);
        return slot;
    }

    public IReadOnlyList<PostSlot> GetCurrentQueue()
    {
        lock (_queueLock) return _queue.ToArray();
    }

    public IReadOnlyList<PostSlot> GetCurrentQueueForPlatform(string platform)
    {
        lock (_queueLock)
            return _queue.Where(s => s.Platforms.Contains(platform, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    public void RemoveFromQueue(PostSlot slot)
    {
        lock (_queueLock) _queue.RemoveAll(s => s.SlotId == slot.SlotId);
    }

    public void MarkPublished(PostSlot slot)
    {
        slot.Status = PostStatus.Published;
        _log.LogInformation("Slot {Id} PUBLISHED on {Platforms}", slot.SlotId, string.Join(",", slot.PerPlatformUrls));
    }
    public void MarkFailed(PostSlot slot, string error)
    {
        slot.Status = PostStatus.Failed;
        slot.Error = error;
        _log.LogError("Slot {Id} FAILED: {Error}", slot.SlotId, error);
    }

    public HookScore ScoreHook(Hook hook, string platform, DateTimeOffset targetTime)
    {
        // Pillar fit: is the hook's primary pillar in the top-2 weights?
        var pillarFit = 0.5;
        if (_pillarWeights.TryGetValue(hook.PrimaryPillar.ToString(), out var w))
            pillarFit = Math.Min(1.0, w.Weight);

        // Platform fit
        var platformFit = hook.BestFor.Length == 0 ? 0.5
            : (hook.BestFor.Any(b => string.Equals(b, platform, StringComparison.OrdinalIgnoreCase)) ? 1.0 : 0.3);

        // Time fit: how close to the nearest scheduled posting slot (take the best match).
        var timeFit = 0.0;
        foreach (var slotStr in _settings.PostingTimesUtc)
        {
            var parts = slotStr.Split(':');
            var slotTime = new TimeSpan(int.Parse(parts[0]), int.Parse(parts[1]), 0);
            var diff = Math.Abs((targetTime.UtcDateTime.TimeOfDay - slotTime).TotalMinutes);
            var thisFit = diff < 30 ? 1.0 : diff < 90 ? 0.6 : 0.0;
            if (thisFit > timeFit) timeFit = thisFit;
        }
        if (timeFit == 0) timeFit = 0.3; // off-slot still publishable, lower priority

        // Conversion potential: hook's own score (0-100) normalized
        var conv = Math.Clamp(hook.Score / 100.0, 0, 1);

        // Weighted composite
        var composite = 0.30 * pillarFit + 0.25 * platformFit + 0.20 * timeFit + 0.25 * conv;

        return new HookScore
        {
            HookId = hook.Id,
            Platform = platform,
            PillarFit = pillarFit,
            PlatformFit = platformFit,
            TimeFit = timeFit,
            ConversionPotential = conv,
            Composite = composite,
            Reason = $"pillar={pillarFit:F2} plat={platformFit:F2} time={timeFit:F2} conv={conv:F2}",
        };
    }

    private string BuildCaption(Hook hook, Pillar pillar)
    {
        var link = UtmLinks.BuildTracked(_settings.LinktreeUrl, hook.Id, pillar.ToString());

        var pillarTag = pillar switch
        {
            Pillar.Identify   => "#IdentifyYourSignal",
            Pillar.Reprogram  => "#ReprogramTheResponse",
            Pillar.Integrate  => "#IntegrateTheRejected",
            Pillar.Stabilise  => "#StabiliseTheSystem",
            _ => "#IRISMethod"
        };

        return
$@"🧠 {hook.Text}

{link}

{pillarTag} #TheInnerShiftLab #IRISMethod #VitiligoHealing #TraumaInformedHealing #HolisticHealing";
    }

    private DateTimeOffset NextPostingTime(DateTimeOffset from)
    {
        var today = from.UtcDateTime.Date;
        foreach (var slotStr in _settings.PostingTimesUtc)
        {
            var parts = slotStr.Split(':');
            var candidate = today.AddHours(int.Parse(parts[0])).AddMinutes(int.Parse(parts[1]));
            if (candidate > from.UtcDateTime)
                return new DateTimeOffset(candidate, TimeSpan.Zero);
        }
        // No more slots today — first slot tomorrow
        var tomorrow = today.AddDays(1);
        var firstParts = _settings.PostingTimesUtc.First().Split(':');
        return new DateTimeOffset(
            tomorrow.AddHours(int.Parse(firstParts[0])).AddMinutes(int.Parse(firstParts[1])),
            TimeSpan.Zero);
    }

    // -- JSON file shapes
    private sealed class HooksFile
    {
        [JsonProperty("hooks")] public List<Hook> Hooks { get; set; } = new();
    }
    private sealed class PillarsFile
    {
        [JsonProperty("pillars")] public List<PillarWeight> Pillars { get; set; } = new();
    }
    private sealed class PillarWeight
    {
        [JsonProperty("pillar")] public string Pillar { get; set; } = "";
        [JsonProperty("weight")] public double Weight { get; set; } = 0.5;
        [JsonProperty("notes")] public string Notes { get; set; } = "";
    }
}
