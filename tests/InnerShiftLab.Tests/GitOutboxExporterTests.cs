// Tests for GitOutboxExporter's pure helpers only. The actual git shell-out and
// force-push are NOT exercised here — CI has no network and no remote to push to,
// so shell-level behaviour is untested by design.
using InnerShiftLab.Outbox;
using Xunit;

namespace InnerShiftLab.Tests;

public class GitOutboxExporterTests : IDisposable
{
    private readonly string _root;

    public GitOutboxExporterTests()
    {
        _root = Directory.CreateTempSubdirectory("iris_gitexport_").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private string MakeDateDir(DateTimeOffset date)
    {
        var dir = Path.Combine(_root, date.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void SelectRetainedDirs_KeepsWithinWindow_DropsOlder()
    {
        var now = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        var today = MakeDateDir(now);
        var edge = MakeDateDir(now.AddDays(-13));   // RetentionDays-1
        MakeDateDir(now.AddDays(-19));               // RetentionDays+5 — dropped

        var retained = GitOutboxExporter.SelectRetainedDirs(_root, retentionDays: 14, now);

        Assert.Equal(2, retained.Count);
        Assert.Contains(today, retained);
        Assert.Contains(edge, retained);
    }

    [Fact]
    public void SelectRetainedDirs_IgnoresNonDateDirs_AndMissingRoot()
    {
        Directory.CreateDirectory(Path.Combine(_root, "not-a-date"));
        MakeDateDir(new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero));

        var now = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        Assert.Single(GitOutboxExporter.SelectRetainedDirs(_root, 14, now));
        Assert.Empty(GitOutboxExporter.SelectRetainedDirs(Path.Combine(_root, "nope"), 14, now));
    }

    [Fact]
    public void SelectRetainedDirs_ExcludesFutureDated()
    {
        var now = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        MakeDateDir(now.AddDays(3)); // a clock-skew future dir must not be selected

        Assert.Empty(GitOutboxExporter.SelectRetainedDirs(_root, 14, now));
    }

    [Fact]
    public void ResolveOwnerRepo_PrefersSettings_ThenEnv_ThenOrigin()
    {
        Assert.Equal(("me", "explicit"),
            GitOutboxExporter.ResolveOwnerRepo("me/explicit", "env/repo", "https://github.com/git/origin.git"));
        Assert.Equal(("env", "repo"),
            GitOutboxExporter.ResolveOwnerRepo("", "env/repo", "https://github.com/git/origin.git"));
        Assert.Equal(("git", "origin"),
            GitOutboxExporter.ResolveOwnerRepo(null, null, "https://github.com/git/origin.git"));
    }

    [Fact]
    public void ResolveOwnerRepo_NoneAvailable_ThrowsWithGuidance()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => GitOutboxExporter.ResolveOwnerRepo("", "", null));
        Assert.Contains("Outbox:Git:Repository", ex.Message);
    }

    [Theory]
    [InlineData("https://github.com/AI7891/IRIS_Headless_Engine.git", "AI7891/IRIS_Headless_Engine")]
    [InlineData("git@github.com:AI7891/IRIS_Headless_Engine.git", "AI7891/IRIS_Headless_Engine")]
    [InlineData("https://github.com/AI7891/IRIS_Headless_Engine", "AI7891/IRIS_Headless_Engine")]
    public void ExtractOwnerRepoFromUrl_ParsesHttpsAndSsh(string url, string expected)
    {
        Assert.Equal(expected, GitOutboxExporter.ExtractOwnerRepoFromUrl(url));
    }

    [Fact]
    public void BuildPickupUrl_EscapesEachFolderSegment()
    {
        var url = GitOutboxExporter.BuildPickupUrl("AI7891", "IRIS_Headless_Engine", "outbox", "2026-07-16/ab cd/pkg");
        Assert.Equal("https://github.com/AI7891/IRIS_Headless_Engine/tree/outbox/2026-07-16/ab%20cd/pkg", url);
    }

    [Fact]
    public void BuildPickupUrl_NormalizesBackslashes()
    {
        var url = GitOutboxExporter.BuildPickupUrl("o", "r", "outbox", "2026-07-16\\pkg");
        Assert.Equal("https://github.com/o/r/tree/outbox/2026-07-16/pkg", url);
    }

    [Fact]
    public void Redact_RemovesAccessTokenFromGitOutput()
    {
        var leaky = "fatal: unable to access 'https://x-access-token:SECRET123@github.com/o/r.git/': 403";
        var clean = GitOutboxExporter.Redact(leaky);

        Assert.DoesNotContain("SECRET123", clean);
        Assert.Contains("x-access-token:***@github.com", clean);
    }

    [Fact]
    public void Redact_LeavesNonTokenTextUntouched()
    {
        const string msg = "fatal: repository not found";
        Assert.Equal(msg, GitOutboxExporter.Redact(msg));
    }
}
