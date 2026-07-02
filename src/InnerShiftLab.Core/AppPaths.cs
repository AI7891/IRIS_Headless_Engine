// =============================================================================
//  AppPaths — single source of truth for where the app reads/writes files.
//  The documented workflow runs the app from src/ with hooks.json / pillars.json /
//  appsettings.json at the repo root, but a published single-file build copies those
//  files next to the binary. Rather than hard-code "../" everywhere (which breaks
//  depending on the launch directory), resolve one consistent root by searching for
//  the marker files, then derive data/ and output/ from it.
// =============================================================================
namespace InnerShiftLab.Core;

public static class AppPaths
{
    private static readonly string[] Markers = { "hooks.json", "appsettings.json", "pillars.json" };

    /// <summary>
    /// Resolves the directory that holds the app's data files. Searches the content
    /// root, its parent, and the current working directory (and its parent) for any
    /// marker file. Falls back to the content root if none is found.
    /// </summary>
    public static string ResolveRoot(string contentRoot)
    {
        var candidates = new[]
        {
            contentRoot,
            Path.Combine(contentRoot, ".."),
            Directory.GetCurrentDirectory(),
            Path.Combine(Directory.GetCurrentDirectory(), ".."),
            AppContext.BaseDirectory,
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (Markers.Any(m => File.Exists(Path.Combine(full, m))))
                return full;
        }
        return Path.GetFullPath(contentRoot);
    }

    /// <summary>Full path to a data file (hooks.json, pillars.json, appsettings.json).</summary>
    public static string DataFile(string root, string fileName) => Path.Combine(root, fileName);

    /// <summary>Directory for runtime state (SQLite db, token vault, heartbeat). Created if missing.</summary>
    public static string DataDir(string root)
    {
        var dir = Path.Combine(root, "data");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Directory for rendered media output. Created if missing.</summary>
    public static string OutputDir(string root)
    {
        var dir = Path.Combine(root, "output");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Rewrites a SQLite connection string so a relative "Data Source" path is anchored to
    /// <paramref name="root"/>. Absolute paths and in-memory sources are left untouched.
    /// </summary>
    public static string ResolveConnectionString(string connString, string root)
    {
        var m = System.Text.RegularExpressions.Regex.Match(connString, @"Data Source=([^;]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return connString;
        var dataSource = m.Groups[1].Value.Trim();
        if (dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(dataSource))
            return connString;
        var resolved = Path.Combine(root, dataSource);
        return connString.Replace(m.Value, $"Data Source={resolved}");
    }
}
