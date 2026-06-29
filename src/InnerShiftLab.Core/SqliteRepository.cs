// =============================================================================
//  Repository — SQLite-backed persistence
//  Tables: tokens, posts, conversions, webhooks, scheduled
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
    Task<IReadOnlyList<PostSlot>> GetPendingPostsAsync();
    Task<IReadOnlyList<PostSlot>> GetPublishedTodayAsync();

    // Webhooks
    Task RecordWebhookAsync(string source, string body);

    // Conversions
    Task SaveConversionAsync(Conversion c);
    Task<IReadOnlyList<Conversion>> GetConversionsAsync(int limit);
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
";
        await cmd.ExecuteNonQueryAsync();
        _initialized = true;
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
        cmd.CommandText = @"INSERT INTO conversions (post_id, platform, utm_campaign, utm_content, event_type, revenue_eur, timestamp)
                            VALUES ($p, $pl, $c, $co, $e, $r, $t)";
        cmd.Parameters.AddWithValue("$p", c.PostId ?? "");
        cmd.Parameters.AddWithValue("$pl", c.Platform ?? "");
        cmd.Parameters.AddWithValue("$c", c.UtmCampaign ?? "");
        cmd.Parameters.AddWithValue("$co", c.UtmContent ?? "");
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
        cmd.CommandText = "SELECT post_id, platform, utm_campaign, utm_content, event_type, revenue_eur, timestamp FROM conversions ORDER BY id DESC LIMIT $l";
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
