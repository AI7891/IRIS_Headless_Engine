// =============================================================================
//  Git outbox exporter — the default, zero-cost delivery mechanism.
//
//  Google Drive export can't work on a personal Google account: service accounts
//  have no storage quota and can't own files, so a folder shared from personal
//  Gmail fails with 403 storageQuotaExceeded (Shared Drives need paid Workspace).
//  Instead we push the package tree to an orphan branch of THIS repo using the
//  ambient Codespaces git credentials — no new secret, no cost — and the operator
//  opens it from the GitHub mobile app.
//
//  The branch is force-pushed as a single squashed root commit every time:
//  git history is deliberately not kept (SQLite is the source of truth, and daily
//  MP4s in permanent history would bloat the repo without bound). Each export
//  mirrors the last RetentionDays of the local outbox root, so it is idempotent
//  and self-healing.
// =============================================================================
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace InnerShiftLab.Outbox;

public sealed class GitOutboxExporter : IPackageExporter
{
    // Guards against DailyOutboxJob and ExportRetryJob overlapping: two concurrent
    // force-pushes to the same branch would race.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly OutboxSettings _settings;
    private readonly string _outboxRoot;
    private readonly string _appRoot;
    private readonly ILogger<GitOutboxExporter> _log;

    private static readonly Regex TokenPattern =
        new("x-access-token:[^@]*@", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public GitOutboxExporter(OutboxSettings settings, string outboxRoot, string appRoot, ILogger<GitOutboxExporter> log)
    {
        _settings = settings; _outboxRoot = outboxRoot; _appRoot = appRoot; _log = log;
    }

    /// <summary>
    /// Resolves the push target once at startup so misconfiguration is loud and early
    /// rather than surfacing as a 09:00 failure and a retry job throwing every 15 minutes.
    /// Returns "owner/repo". Throws InvalidOperationException with operator guidance if
    /// unresolvable. Does not push or clone anything.
    /// </summary>
    public async Task<string> ValidateTargetAsync(CancellationToken ct = default)
    {
        var (owner, repo) = await ResolveRepositoryAsync(ct);
        return $"{owner}/{repo}";
    }

    public async Task<string> ExportAsync(OutboxPackage package, CancellationToken ct = default)
    {
        var (owner, repo) = await ResolveRepositoryAsync(ct);
        var branch = _settings.Git.Branch;

        await _gate.WaitAsync(ct);
        var temp = Path.Combine(Path.GetTempPath(), $"iris-outbox-{Guid.NewGuid():N}");
        try
        {
            var retained = SelectRetainedDirs(_outboxRoot, _settings.Git.RetentionDays, DateTimeOffset.UtcNow);
            if (retained.Count == 0)
            {
                _log.LogWarning("Git export: no retained package directories under {Root}; leaving package local at {Dir}",
                    _outboxRoot, package.PackageDir);
                return package.PackageDir;
            }

            Directory.CreateDirectory(temp);
            await RunGitAsync(new[] { "init", "-q" }, temp, ct);
            await RunGitAsync(new[] { "checkout", "-q", "--orphan", branch }, temp, ct);

            foreach (var dir in retained)
                CopyDirectory(dir, Path.Combine(temp, Path.GetFileName(dir)));
            await File.WriteAllTextAsync(Path.Combine(temp, "README.md"), BranchReadme(branch), ct);

            await RunGitAsync(new[] { "add", "-A" }, temp, ct);
            var message = $"IRIS outbox {DateTimeOffset.UtcNow:yyyy-MM-dd} ({retained.Count} day(s))";
            await RunGitAsync(new[]
            {
                "-c", "user.name=IRIS Engine",
                "-c", "user.email=iris@innershiftlab.local",
                "commit", "-q", "-m", message,
            }, temp, ct);

            // Push to a plain (credential-free) URL. When GITHUB_TOKEN is set, auth goes
            // through a git credential helper that reads the token from the inherited
            // environment — so the secret is NEVER placed in the process argument list
            // (which is visible in `ps` to anything else on the box).
            var pushArgs = new List<string>();
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_TOKEN")))
            {
                // The empty helper resets any inherited helper chain so ours wins; the
                // "!f() { ... }" helper is run by git via sh, which expands $GITHUB_TOKEN
                // from the inherited env — the value is never interpolated on our side.
                pushArgs.Add("-c"); pushArgs.Add("credential.helper=");
                pushArgs.Add("-c"); pushArgs.Add(
                    "credential.helper=!f() { echo username=x-access-token; echo \"password=$GITHUB_TOKEN\"; }; f");
            }
            pushArgs.Add("push"); pushArgs.Add("--force"); pushArgs.Add("-q");
            pushArgs.Add(BuildRemoteUrl(owner, repo)); pushArgs.Add(branch);
            await RunGitAsync(pushArgs.ToArray(), temp, ct);

            var relative = Path.GetRelativePath(_outboxRoot, package.PackageDir);
            var url = BuildPickupUrl(owner, repo, branch, relative);
            _log.LogInformation("Outbox package {PackageId} pushed to {Owner}/{Repo}@{Branch}: {Url}",
                package.PackageId, owner, repo, branch, url);
            return url;
        }
        finally
        {
            _gate.Release();
            TryDelete(temp);
        }
    }

    // -- testable helpers -----------------------------------------------------

    /// <summary>
    /// Selects the package/date directories under <paramref name="outboxRoot"/> whose
    /// leading yyyy-MM-dd name is within <paramref name="retentionDays"/> of today.
    /// Directories without a parseable date prefix are ignored.
    /// </summary>
    internal static List<string> SelectRetainedDirs(string outboxRoot, int retentionDays, DateTimeOffset nowUtc)
    {
        if (!Directory.Exists(outboxRoot)) return new List<string>();
        var cutoff = DateOnly.FromDateTime(nowUtc.UtcDateTime).AddDays(-Math.Max(0, retentionDays));
        var today = DateOnly.FromDateTime(nowUtc.UtcDateTime);
        return Directory.EnumerateDirectories(outboxRoot)
            .Where(d => TryParseDatePrefix(Path.GetFileName(d), out var date) && date >= cutoff && date <= today)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
    }

    private static bool TryParseDatePrefix(string name, out DateOnly date)
    {
        // Folder names are "yyyy-MM-dd"; tolerate a trailing suffix just in case.
        var head = name.Length >= 10 ? name[..10] : name;
        return DateOnly.TryParseExact(head, "yyyy-MM-dd", out date);
    }

    /// <summary>owner/repo resolution precedence: explicit setting → env var → origin remote → throw.</summary>
    internal static (string Owner, string Repo) ResolveOwnerRepo(string? settingsRepo, string? envRepo, string? originUrl)
    {
        foreach (var candidate in new[] { settingsRepo, envRepo, ExtractOwnerRepoFromUrl(originUrl) })
        {
            if (TryParseOwnerRepo(candidate, out var owner, out var repo))
                return (owner, repo);
        }
        throw new InvalidOperationException(
            "Git export could not determine the target repository. Set Outbox:Git:Repository " +
            "to 'owner/repo', or ensure GITHUB_REPOSITORY is set (Codespaces/Actions do this).");
    }

    private static bool TryParseOwnerRepo(string? value, out string owner, out string repo)
    {
        owner = ""; repo = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        owner = parts[0];
        repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        return owner.Length > 0 && repo.Length > 0;
    }

    /// <summary>
    /// True when owner/repo is the source code repo (from GITHUB_REPOSITORY or the origin
    /// remote), meaning the outbox media would land on a branch of the code repo and bloat
    /// every future clone. Comparison is case-insensitive.
    /// </summary>
    internal static bool IsSameRepo(string owner, string repo, string? sourceOwnerRepo)
    {
        if (!TryParseOwnerRepo(sourceOwnerRepo, out var srcOwner, out var srcRepo)) return false;
        return string.Equals(owner, srcOwner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(repo, srcRepo, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Pulls "owner/repo" out of an https or ssh git remote URL.</summary>
    internal static string? ExtractOwnerRepoFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        var m = Regex.Match(url, @"github\.com[/:]([^/]+)/(.+?)(?:\.git)?/?$", RegexOptions.IgnoreCase);
        return m.Success ? $"{m.Groups[1].Value}/{m.Groups[2].Value}" : null;
    }

    /// <summary>GitHub tree URL to the package folder; each path segment is URL-escaped.</summary>
    internal static string BuildPickupUrl(string owner, string repo, string branch, string relativeFolder)
    {
        var segments = relativeFolder
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        var folderPath = string.Join('/', segments);
        return $"https://github.com/{owner}/{repo}/tree/{Uri.EscapeDataString(branch)}/{folderPath}";
    }

    /// <summary>Redacts any embedded x-access-token credential so it never reaches a log or exception.</summary>
    internal static string Redact(string text)
        => string.IsNullOrEmpty(text) ? text : TokenPattern.Replace(text, "x-access-token:***@");

    // -- git plumbing ---------------------------------------------------------

    private async Task<(string Owner, string Repo)> ResolveRepositoryAsync(CancellationToken ct)
    {
        string? originUrl = null;
        if (string.IsNullOrWhiteSpace(_settings.Git.Repository) &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_REPOSITORY")))
        {
            try { originUrl = (await RunGitAsync(new[] { "remote", "get-url", "origin" }, _appRoot, ct)).Trim(); }
            catch (Exception ex) { _log.LogDebug("Git export: could not read origin remote: {Msg}", ex.Message); }
        }
        return ResolveOwnerRepo(_settings.Git.Repository,
            Environment.GetEnvironmentVariable("GITHUB_REPOSITORY"), originUrl);
    }

    /// <summary>
    /// The push URL — always plain, never credential-bearing. A token, when present, is
    /// supplied out-of-band via a git credential helper (see the push call) so it never
    /// lands in the process argument list.
    /// </summary>
    internal static string BuildRemoteUrl(string owner, string repo)
        => $"https://github.com/{owner}/{repo}.git";

    private async Task<string> RunGitAsync(string[] args, string workingDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git (is it installed and on PATH?)");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        try
        {
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new InvalidOperationException("git timed out after 2 minutes");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {args[0]} failed (exit {proc.ExitCode}): {Redact(stderr).Trim()}");
        return stdout;
    }

    private static string BranchReadme(string branch)
    {
        var lines = new[]
        {
            "# IRIS Outbox",
            "",
            $"This orphan branch (`{branch}`) is the phone pickup point for the IRIS headless",
            "content factory (it may live in the code repo or a dedicated outbox repo). Each",
            "dated folder holds one or more ready-to-post packages — media plus",
            "caption/title/description/link text files and a `manifest.json`.",
            "",
            "**It is force-pushed as a single squashed commit on every export and is NOT code.**",
            "Do not merge it. SQLite remains the source of truth; only the last few days of",
            "packages are kept here.",
            "",
            "Open a package folder in the GitHub mobile app, post each platform manually, then",
            "confirm via `POST /api/outbox/<packageId>/<platform>/confirm`.",
            "",
        };
        return string.Join('\n', lines);
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.EnumerateDirectories(source))
            CopyDirectory(sub, Path.Combine(dest, Path.GetFileName(sub)));
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* temp dir cleanup is best effort */ }
    }
}
