// =============================================================================
//  Outbox retention helpers — the pure arithmetic/path logic behind RetentionJob,
//  kept here so it is unit-testable without a filesystem or a scheduler.
// =============================================================================
namespace InnerShiftLab.Outbox;

public static class OutboxRetention
{
    /// <summary>
    /// FIFO size-pass selection: given packages oldest-first with their on-disk sizes, the
    /// current total, and the cap, returns the ids to prune — oldest first, stopping as soon
    /// as the running total would be at or under the cap. Never over-prunes: once under the
    /// cap it selects nothing more, so the newest packages survive.
    /// </summary>
    internal static List<string> SelectForSizePass(
        IReadOnlyList<(string PackageId, long Bytes)> oldestFirst, long currentBytes, long capBytes)
    {
        var toPrune = new List<string>();
        if (capBytes <= 0) return toPrune; // 0 = no size cap
        var running = currentBytes;
        foreach (var (id, bytes) in oldestFirst)
        {
            if (running <= capBytes) break;
            toPrune.Add(id);
            running -= bytes;
        }
        return toPrune;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> resolves to a path inside <paramref name="root"/>
    /// (or is the root itself). Guards recursive deletes from ever escaping the outbox root.
    /// </summary>
    internal static bool IsInsideRoot(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate)) return false;
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        if (string.Equals(candFull, rootFull, StringComparison.Ordinal)) return true;
        return candFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>Total size in bytes of a directory tree (0 if it doesn't exist).</summary>
    internal static long DirectorySizeBytes(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(f).Length; } catch { /* file vanished mid-scan */ }
        }
        return total;
    }
}
