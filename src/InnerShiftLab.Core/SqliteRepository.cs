// =============================================================================
//  Repository — SQLite-backed persistence
//  Tables: tokens, posts, conversions, webhooks, outbox
// =============================================================================
using InnerShiftLab.Core;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace InnerShiftLab.Core;

public interface IRepository
{
    Task InitAsync();
    Task<bool> PingAsync();

    // Tokens
    Task SaveTokensAsync(string provider, string json);
    Task<string?> LoadTokensAsync(string provider);

    // Posts
    Task SavePostAsync(PostSlot slot);
    Task<PostSlot?> GetPostAsync(string slotId);
    Task<IReadOnlyList<PostSlot>> GetPendingPostsAsync();
    Task<IReadOnlyList<PostSlot>> GetPublishedTodayAsync();

    // Webhooks
    Task RecordWebhookAsync(string source, string body);

    // Conversions
    Task SaveConversionAsync(Conversion c);
    Task<IReadOnlyList<Conversion>> GetConversionsAsync(int limit);

    // Outbox — human-in-the-loop packages awaiting manual posting
    Task SaveOutboxItemAsync(OutboxItem item);
    Task<IReadOnlyList<OutboxItem>> GetOutboxItemsAsync(OutboxStatus? status = null, int limit = 100);
    Task<IReadOnlyList<OutboxItem>> GetOutboxPackageAsync(string packageId);
    Task<IReadOnlyList<string>> GetUnexportedPackageIdsAsync(int limit = 20);
    Task<int> MarkOutboxExportedAsync(string packageId, string exportRef);
    Task<bool> MarkOutboxPostedAsync(string packageId, string platform, string? postUrl);
    Task<bool> MarkOutboxSkippedAsync(string packageId, string platform);
}

public sealed class SqliteRepository : IRepository, IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly string _connStr;
    private bool _initialized;

    public SqliteRepository(string? conn)
    {
        // Allow either a full connection string or a Data Source path
        _connStr = conn ?? "Data Source=data/iris.db";
        var pathMatch = System.Text.RegularExpressions.Regex.Match(_connStr, "Data Source=([^;]+)");
        _dbPath = pathMatch.Success ? pathMatch.Groups[1].Value : "data/iris.db";
    }

    public async Task InitAsync()
    {
        if (_initialized) return;
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS tokens (
    provider TEXT PRIMARY KEY,
    payload  TEXT NOT NULL,
    updated  TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS posts (
    slot_id     TEXT PRIMARY KEY,
    hook_id     TEXT,
    pillar      TEXT,
    platforms   TEXT,
    caption     TEXT,
    media_url   TEXT,
    scheduled   TEXT,
    status      TEXT,
    post_ids    TEXT,
    post_urls   TEXT,
    error       TEXT
);
CREATE TABLE IF NOT EXISTS conversions (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    post_id       TEXT,
    platform      TEXT,
    utm_campaign  TEXT,
    utm_content   TEXT,
    utm_source    TEXT NOT NULL DEFAULT '',
    event_type    TEXT,
    revenue_eur   REAL,
    timestamp     TEXT
);
CREATE TABLE IF NOT EXISTS webhooks (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    source    TEXT,
    body      TEXT,
    received  TEXT
);
CREATE TABLE IF NOT EXISTS outbox (
    package_id  TEXT NOT NULL,
    platform    TEXT NOT NULL,
    hook_id     TEXT,
    pillar      TEXT,
    caption     TEXT,
    title       TEXT,
    media_path  TEXT,
    package_dir TEXT NOT NULL DEFAULT '',
    width       INTEGER,
    height      INTEGER,
    status      TEXT,
    export_ref  TEXT,
    created     TEXT,
    posted_at   TEXT,
    post_url    TEXT,
    PRIMARY KEY (package_id, platform)
);
";
        await cmd.ExecuteNonQueryAsync();

        // Defensive migrations for databases created before a column existed.
        await EnsureColumnAsync(conn, "outbox", "package_dir", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(conn, "conversions", "utm_source", "TEXT NOT NULL DEFAULT ''");

        _initialized = true;
    }

    private static async Task EnsureColumnAsync(SqliteConnection conn, string table, string column, string definition)
    {
        await using var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{column}'";
        if (Convert.ToInt32(await check.ExecuteScalarAsync()) > 0) return;
        await using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        await alter.ExecuteNonQueryAsync();
    }

    public async Task<bool> PingAsync()
    {
        try
        {
            await using var conn = Open();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            var r = await cmd.ExecuteScalarAsync();
            return r != null && Convert.ToInt32(r) == 1;
        }
        catch { return false; }
    }

    public async Task SaveTokensAsync(string provider, string json)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO tokens (provider, payload, updated) VALUES ($p, $j, $u)
                            ON CONFLICT(provider) DO UPDATE SET payload=$j, updated=$u";
        cmd.Parameters.AddWithValue("$p", provider);
        cmd.Parameters.AddWithValue("$j", json);
        cmd.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<string?> LoadTokensAsync(string provider)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload FROM tokens WHERE provider=$p";
        cmd.Parameters.AddWithValue("$p", provider);
        var r = await cmd.ExecuteScalarAsync();
        return r as string;
    }

    public async Task SavePostAsync(PostSlot slot)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO posts (slot_id, hook_id, pillar, platforms, caption, media_url, scheduled, status, post_ids, post_urls, error)
                            VALUES ($s, $h, $p, $pl, $c, $m, $sc, $st, $pi, $pu, $e)
                            ON CONFLICT(slot_id) DO UPDATE SET status=$st, post_ids=$pi, post_urls=$pu, error=$e";
        cmd.Parameters.AddWithValue("$s", slot.SlotId);
        cmd.Parameters.AddWithValue("$h", slot.HookId ?? "");
        cmd.Parameters.AddWithValue("$p", slot.Pillar.ToString());
        cmd.Parameters.AddWithValue("$pl", string.Join(",", slot.Platforms));
        cmd.Parameters.AddWithValue("$c", slot.Caption ?? "");
        cmd.Parameters.AddWithValue("$m", slot.MediaUrl ?? "");
        cmd.Parameters.AddWithValue("$sc", slot.ScheduledAt.ToString("O"));
        cmd.Parameters.AddWithValue("$st", slot.Status.ToString());
        cmd.Parameters.AddWithValue("$pi", string.Join(",", slot.PerPlatformPostIds));
        cmd.Parameters.AddWithValue("$pu", string.Join(",", slot.PerPlatformUrls));
        cmd.Parameters.AddWithValue("$e", slot.Error ?? "");
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<PostSlot?> GetPostAsync(string slotId)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT slot_id, hook_id, pillar, platforms, caption, media_url, scheduled, status, post_ids, post_urls, error FROM posts WHERE slot_id=$s";
        cmd.Parameters.AddWithValue("$s", slotId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new PostSlot
        {
            SlotId = r.GetString(0),
            HookId = r.IsDBNull(1) ? "" : r.GetString(1),
            Pillar = Enum.TryParse<Pillar>(r.IsDBNull(2) ? "" : r.GetString(2), out var p) ? p : Pillar.Integrate,
            Platforms = (r.IsDBNull(3) ? "" : r.GetString(3)).Split(',', StringSplitOptions.RemoveEmptyEntries),
            Caption = r.IsDBNull(4) ? "" : r.GetString(4),
            MediaUrl = r.IsDBNull(5) ? null : r.GetString(5),
            ScheduledAt = DateTimeOffset.TryParse(r.IsDBNull(6) ? null : r.GetString(6), out var dt) ? dt : DateTimeOffset.UtcNow,
            Status = Enum.TryParse<PostStatus>(r.IsDBNull(7) ? "" : r.GetString(7), out var st) ? st : PostStatus.Queued,
            PerPlatformPostIds = (r.IsDBNull(8) ? "" : r.GetString(8)).Split(',', StringSplitOptions.RemoveEmptyEntries),
            PerPlatformUrls = (r.IsDBNull(9) ? "" : r.GetString(9)).Split(',', StringSplitOptions.RemoveEmptyEntries),
            Error = r.IsDBNull(10) ? null : r.GetString(10),
        };
    }

    public async Task<IReadOnlyList<PostSlot>> GetPendingPostsAsync()
    {
        var list = new List<PostSlot>();
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT slot_id, hook_id, pillar, platforms, caption, media_url, scheduled, status, post_ids, post_urls, error FROM posts WHERE status IN ('Queued','Publishing')";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new PostSlot
            {
                SlotId = r.GetString(0),
                HookId = r.IsDBNull(1) ? "" : r.GetString(1),
                Pillar = Enum.TryParse<Pillar>(r.IsDBNull(2) ? "" : r.GetString(2), out var p) ? p : Pillar.Integrate,
                Platforms = (r.IsDBNull(3) ? "" : r.GetString(3)).Split(',', StringSplitOptions.RemoveEmptyEntries),
                Caption = r.IsDBNull(4) ? "" : r.GetString(4),
                MediaUrl = r.IsDBNull(5) ? null : r.GetString(5),
                ScheduledAt = DateTimeOffset.TryParse(r.IsDBNull(6) ? null : r.GetString(6), out var dt) ? dt : DateTimeOffset.UtcNow,
                Status = Enum.TryParse<PostStatus>(r.IsDBNull(7) ? "" : r.GetString(7), out var st) ? st : PostStatus.Queued,
                PerPlatformPostIds = (r.IsDBNull(8) ? "" : r.GetString(8)).Split(',', StringSplitOptions.RemoveEmptyEntries),
                PerPlatformUrls = (r.IsDBNull(9) ? "" : r.GetString(9)).Split(',', StringSplitOptions.RemoveEmptyEntries),
                Error = r.IsDBNull(10) ? null : r.GetString(10),
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<PostSlot>> GetPublishedTodayAsync()
    {
        var list = new List<PostSlot>();
        var dayStart = DateTimeOffset.UtcNow.Date.ToString("O");
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT slot_id, hook_id, pillar, platforms, caption, media_url, scheduled, status, post_ids, post_urls, error FROM posts WHERE status='Published' AND scheduled >= $d";
        cmd.Parameters.AddWithValue("$d", dayStart);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new PostSlot
            {
                SlotId = r.GetString(0),
                HookId = r.IsDBNull(1) ? "" : r.GetString(1),
                Pillar = Enum.TryParse<Pillar>(r.IsDBNull(2) ? "" : r.GetString(2), out var p) ? p : Pillar.Integrate,
                Platforms = (r.IsDBNull(3) ? "" : r.GetString(3)).Split(',', StringSplitOptions.RemoveEmptyEntries),
                Caption = r.IsDBNull(4) ? "" : r.GetString(4),
                MediaUrl = r.IsDBNull(5) ? null : r.GetString(5),
                ScheduledAt = DateTimeOffset.TryParse(r.IsDBNull(6) ? null : r.GetString(6), out var dt) ? dt : DateTimeOffset.UtcNow,
                Status = PostStatus.Published,
                PerPlatformPostIds = (r.IsDBNull(8) ? "" : r.GetString(8)).Split(',', StringSplitOptions.RemoveEmptyEntries),
                PerPlatformUrls = (r.IsDBNull(9) ? "" : r.GetString(9)).Split(',', StringSplitOptions.RemoveEmptyEntries),
                Error = r.IsDBNull(10) ? null : r.GetString(10),
            });
        }
        return list;
    }

    public async Task RecordWebhookAsync(string source, string body)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO webhooks (source, body, received) VALUES ($s, $b, $r)";
        cmd.Parameters.AddWithValue("$s", source);
        cmd.Parameters.AddWithValue("$b", body);
        cmd.Parameters.AddWithValue("$r", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SaveConversionAsync(Conversion c)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO conversions (post_id, platform, utm_campaign, utm_content, utm_source, event_type, revenue_eur, timestamp)
                            VALUES ($p, $pl, $c, $co, $so, $e, $r, $t)";
        cmd.Parameters.AddWithValue("$p", c.PostId ?? "");
        cmd.Parameters.AddWithValue("$pl", c.Platform ?? "");
        cmd.Parameters.AddWithValue("$c", c.UtmCampaign ?? "");
        cmd.Parameters.AddWithValue("$co", c.UtmContent ?? "");
        cmd.Parameters.AddWithValue("$so", c.UtmSource ?? "");
        cmd.Parameters.AddWithValue("$e", c.EventType ?? "");
        cmd.Parameters.AddWithValue("$r", (object?)c.RevenueEur ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", c.Timestamp.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<Conversion>> GetConversionsAsync(int limit)
    {
        var list = new List<Conversion>();
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT post_id, platform, utm_campaign, utm_content, event_type, revenue_eur, timestamp, utm_source FROM conversions ORDER BY id DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$l", limit);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new Conversion
            {
                PostId = r.IsDBNull(0) ? "" : r.GetString(0),
                Platform = r.IsDBNull(1) ? "" : r.GetString(1),
                UtmCampaign = r.IsDBNull(2) ? "" : r.GetString(2),
                UtmContent = r.IsDBNull(3) ? "" : r.GetString(3),
                EventType = r.IsDBNull(4) ? "" : r.GetString(4),
                RevenueEur = r.IsDBNull(5) ? null : (decimal?)r.GetDouble(5),
                Timestamp = DateTimeOffset.TryParse(r.IsDBNull(6) ? null : r.GetString(6), out var dt) ? dt : DateTimeOffset.UtcNow,
                UtmSource = r.IsDBNull(7) ? "" : r.GetString(7),
            });
        }
        return list;
    }

    public async Task SaveOutboxItemAsync(OutboxItem item)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        // Terminal states never regress: rebuilding a package must not flip a
        // Posted/Skipped variant back to Pending or erase its confirmation.
        cmd.CommandText = @"INSERT INTO outbox (package_id, platform, hook_id, pillar, caption, title, media_path, package_dir, width, height, status, export_ref, created, posted_at, post_url)
                            VALUES ($pk, $pl, $h, $pi, $c, $ti, $m, $pd, $w, $he, $st, $ex, $cr, $po, $pu)
                            ON CONFLICT(package_id, platform) DO UPDATE SET
                                caption=$c, title=$ti, media_path=$m, package_dir=$pd, width=$w, height=$he,
                                status = CASE WHEN outbox.status IN ('Posted','Skipped') THEN outbox.status ELSE $st END,
                                export_ref=$ex,
                                posted_at = COALESCE(outbox.posted_at, $po),
                                post_url = COALESCE(outbox.post_url, $pu)";
        cmd.Parameters.AddWithValue("$pk", item.PackageId);
        cmd.Parameters.AddWithValue("$pl", item.Platform);
        cmd.Parameters.AddWithValue("$h", item.HookId ?? "");
        cmd.Parameters.AddWithValue("$pi", item.Pillar.ToString());
        cmd.Parameters.AddWithValue("$c", item.Caption ?? "");
        cmd.Parameters.AddWithValue("$ti", item.Title ?? "");
        cmd.Parameters.AddWithValue("$m", item.MediaPath ?? "");
        cmd.Parameters.AddWithValue("$pd", item.PackageDir ?? "");
        cmd.Parameters.AddWithValue("$w", item.Width);
        cmd.Parameters.AddWithValue("$he", item.Height);
        cmd.Parameters.AddWithValue("$st", item.Status.ToString());
        cmd.Parameters.AddWithValue("$ex", (object?)item.ExportRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cr", item.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$po", (object?)item.PostedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pu", (object?)item.PostUrl ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<OutboxItem>> GetOutboxItemsAsync(OutboxStatus? status = null, int limit = 100)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        if (status.HasValue)
        {
            cmd.CommandText = $"SELECT {OutboxColumns} FROM outbox WHERE status=$st ORDER BY created DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$st", status.Value.ToString());
        }
        else
        {
            cmd.CommandText = $"SELECT {OutboxColumns} FROM outbox ORDER BY created DESC LIMIT $l";
        }
        cmd.Parameters.AddWithValue("$l", limit);
        return await ReadOutboxItemsAsync(cmd);
    }

    public async Task<IReadOnlyList<OutboxItem>> GetOutboxPackageAsync(string packageId)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {OutboxColumns} FROM outbox WHERE package_id=$pk ORDER BY platform";
        cmd.Parameters.AddWithValue("$pk", packageId);
        return await ReadOutboxItemsAsync(cmd);
    }

    public async Task<IReadOnlyList<string>> GetUnexportedPackageIdsAsync(int limit = 20)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT package_id FROM outbox WHERE status=$st ORDER BY created LIMIT $l";
        cmd.Parameters.AddWithValue("$st", OutboxStatus.Pending.ToString());
        cmd.Parameters.AddWithValue("$l", limit);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(r.GetString(0));
        return list;
    }

    public async Task<int> MarkOutboxExportedAsync(string packageId, string exportRef)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        // Posted/Skipped variants keep their terminal status; only Pending ones become Exported.
        cmd.CommandText = @"UPDATE outbox SET status=$st, export_ref=$ex
                            WHERE package_id=$pk AND status=$pending";
        cmd.Parameters.AddWithValue("$st", OutboxStatus.Exported.ToString());
        cmd.Parameters.AddWithValue("$ex", exportRef);
        cmd.Parameters.AddWithValue("$pk", packageId);
        cmd.Parameters.AddWithValue("$pending", OutboxStatus.Pending.ToString());
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> MarkOutboxPostedAsync(string packageId, string platform, string? postUrl)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        // A Skipped variant stays skipped — confirming it is operator error.
        cmd.CommandText = @"UPDATE outbox SET status=$st, posted_at=$po, post_url=$pu
                            WHERE package_id=$pk AND platform=$pl AND status != $skipped";
        cmd.Parameters.AddWithValue("$st", OutboxStatus.Posted.ToString());
        cmd.Parameters.AddWithValue("$po", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$pu", (object?)postUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pk", packageId);
        cmd.Parameters.AddWithValue("$pl", platform);
        cmd.Parameters.AddWithValue("$skipped", OutboxStatus.Skipped.ToString());
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> MarkOutboxSkippedAsync(string packageId, string platform)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        // A Posted variant stays posted — the post exists in the real world.
        cmd.CommandText = @"UPDATE outbox SET status=$st
                            WHERE package_id=$pk AND platform=$pl AND status != $posted";
        cmd.Parameters.AddWithValue("$st", OutboxStatus.Skipped.ToString());
        cmd.Parameters.AddWithValue("$pk", packageId);
        cmd.Parameters.AddWithValue("$pl", platform);
        cmd.Parameters.AddWithValue("$posted", OutboxStatus.Posted.ToString());
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    private const string OutboxColumns =
        "package_id, platform, hook_id, pillar, caption, title, media_path, package_dir, width, height, status, export_ref, created, posted_at, post_url";

    private static async Task<IReadOnlyList<OutboxItem>> ReadOutboxItemsAsync(SqliteCommand cmd)
    {
        var list = new List<OutboxItem>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new OutboxItem
            {
                PackageId = r.GetString(0),
                Platform = r.GetString(1),
                HookId = r.IsDBNull(2) ? "" : r.GetString(2),
                Pillar = Enum.TryParse<Pillar>(r.IsDBNull(3) ? "" : r.GetString(3), out var p) ? p : Pillar.Integrate,
                Caption = r.IsDBNull(4) ? "" : r.GetString(4),
                Title = r.IsDBNull(5) ? "" : r.GetString(5),
                MediaPath = r.IsDBNull(6) ? "" : r.GetString(6),
                PackageDir = r.IsDBNull(7) ? "" : r.GetString(7),
                Width = r.IsDBNull(8) ? 0 : r.GetInt32(8),
                Height = r.IsDBNull(9) ? 0 : r.GetInt32(9),
                Status = Enum.TryParse<OutboxStatus>(r.IsDBNull(10) ? "" : r.GetString(10), out var st) ? st : OutboxStatus.Pending,
                ExportRef = r.IsDBNull(11) ? null : r.GetString(11),
                CreatedAt = DateTimeOffset.TryParse(r.IsDBNull(12) ? null : r.GetString(12), out var cr) ? cr : DateTimeOffset.UtcNow,
                PostedAt = DateTimeOffset.TryParse(r.IsDBNull(13) ? null : r.GetString(13), out var po) ? po : (DateTimeOffset?)null,
                PostUrl = r.IsDBNull(14) ? null : r.GetString(14),
            });
        }
        return list;
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connStr);
        c.Open();
        return c;
    }

    public async ValueTask DisposeAsync()
    {
        await Task.CompletedTask;
    }
}
