using InnerShiftLab.Core;
using InnerShiftLab.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace InnerShiftLab.Tests;

public class IrisEngineTests
{
    // The repo root holds hooks.json / pillars.json; tests run from the test bin dir,
    // so walk up to the repo root explicitly.
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "hooks.json")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IrisEngine NewEngine(IrisSettings? settings = null)
    {
        var opts = Options.Create(settings ?? new IrisSettings
        {
            PostingTimesUtc = new[] { "09:00", "14:00", "19:00" },
            LinktreeUrl = "https://linktr.ee/test",
        });
        return new IrisEngine(opts, NullLogger<IrisEngine>.Instance, new TestEnv(RepoRoot()));
    }

    [Fact]
    public void LoadsHooksFromRepoRoot()
    {
        var engine = NewEngine();
        Assert.NotEmpty(engine.GetAllHooks());
    }

    [Fact]
    public void Enqueue_AddsSlot_AndRemoveFromQueue_Removes()
    {
        var engine = NewEngine();
        var hook = engine.GetAllHooks().First();

        var slot = engine.Enqueue(hook.Id, Pillar.Identify, new[] { "instagram" });
        Assert.Contains(engine.GetCurrentQueue(), s => s.SlotId == slot.SlotId);

        engine.RemoveFromQueue(slot);
        Assert.DoesNotContain(engine.GetCurrentQueue(), s => s.SlotId == slot.SlotId);
    }

    [Fact]
    public void Enqueue_UnknownHook_Throws()
    {
        var engine = NewEngine();
        Assert.Throws<KeyNotFoundException>(() => engine.Enqueue("does-not-exist", Pillar.Identify, new[] { "instagram" }));
    }

    [Fact]
    public void Enqueue_Caption_ContainsUtmLinkAndPillarTag()
    {
        var engine = NewEngine();
        var hook = engine.GetAllHooks().First();

        var slot = engine.Enqueue(hook.Id, Pillar.Stabilise, new[] { "instagram" });

        Assert.Contains("linktr.ee/test", slot.Caption);
        Assert.Contains("utm_campaign=" + hook.Id, slot.Caption);
        Assert.Contains("utm_content=Stabilise", slot.Caption);
        Assert.Contains("#StabiliseTheSystem", slot.Caption);
        Assert.Contains(hook.Text, slot.Caption);
    }

    [Fact]
    public void Enqueue_SchedulesAtAConfiguredPostingTime()
    {
        var engine = NewEngine();
        var hook = engine.GetAllHooks().First();

        var slot = engine.Enqueue(hook.Id, Pillar.Identify, new[] { "instagram" });

        // Scheduled time must land exactly on one of the configured UTC slots.
        var hhmm = slot.ScheduledAt.UtcDateTime.ToString("HH:mm");
        Assert.Contains(hhmm, new[] { "09:00", "14:00", "19:00" });
        Assert.True(slot.ScheduledAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void ScoreHook_CompositeIsBetweenZeroAndOne()
    {
        var engine = NewEngine();
        var hook = engine.GetAllHooks().First();

        var score = engine.ScoreHook(hook, "instagram", DateTimeOffset.UtcNow);

        Assert.InRange(score.Composite, 0.0, 1.0);
        Assert.InRange(score.PillarFit, 0.0, 1.0);
        Assert.InRange(score.PlatformFit, 0.0, 1.0);
        Assert.InRange(score.TimeFit, 0.0, 1.0);
        Assert.InRange(score.ConversionPotential, 0.0, 1.0);
    }

    [Fact]
    public void ScoreHook_PlatformInBestFor_ScoresHigherThanUnmatched()
    {
        var engine = NewEngine();
        var hook = engine.GetAllHooks().First(h => h.BestFor.Length > 0);
        var good = hook.BestFor[0];
        var bad = "myspace";

        var t = new DateTimeOffset(DateTime.UtcNow.Date.AddHours(9), TimeSpan.Zero);
        var s1 = engine.ScoreHook(hook, good, t);
        var s2 = engine.ScoreHook(hook, bad, t);

        Assert.True(s1.PlatformFit > s2.PlatformFit);
        Assert.True(s1.Composite > s2.Composite);
    }

    [Fact]
    public void ScoreHook_OnSlotTime_HasFullTimeFit()
    {
        var engine = NewEngine();
        var hook = engine.GetAllHooks().First();

        var onSlot = new DateTimeOffset(DateTime.UtcNow.Date.AddHours(9), TimeSpan.Zero);
        Assert.Equal(1.0, engine.ScoreHook(hook, "instagram", onSlot).TimeFit);

        var offSlot = new DateTimeOffset(DateTime.UtcNow.Date.AddHours(3), TimeSpan.Zero);
        Assert.True(engine.ScoreHook(hook, "instagram", offSlot).TimeFit < 1.0);
    }
}
