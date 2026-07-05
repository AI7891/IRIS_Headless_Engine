// =============================================================================
//  Draft repository — SQLite persistence for the PostDrafts approval queue.
//  Lives in the same iris.db as the rest of the pipeline state. Transitions
//  are guarded UPDATEs (WHERE Status = expected) so the state machine cannot
//  be bypassed even by concurrent callers.
// =============================================================================
using Microsoft.Data.Sqlite;

namespace InnerShiftLab.Core;

public interface IDraftRepository
{
    Task InitAsync();
    Task<PostDraft> CreateAsync(PostDraft draft);
    Task<PostDraft?> GetAsync(long id);
    Task<IReadOnlyList<PostDraft>> ListAsync(DraftStatus? status = null, int limit = 100);
    Task<int> CountCreatedTodayAsync(string platform);

    /// <summary>
    /// Atomically moves a draft from <paramref name="from"/> to <paramref name="to"/>.
    /// Throws if the transition is not legal; returns false if the draft was not in
    /// the expected state (e.g. someone else transitioned it first).
    /// </summary>
    Task<bool> TransitionAsync(long id, DraftStatus from, DraftStatus to,
        string? platformPostId = null, string? errorMessage = null);
}

public sealed class SqliteDraftRepository : IDraftRepository
{
    // Migration 002 — also available as scripts/migrations/002_post_drafts.up.sql
    // (rollback: 002_post_drafts.down.sql). Applied idempotently at startup.
    public const string MigrationUp = @"
CREATE TABLE IF NOT EXISTS PostDrafts (
    Id             INTEGER PRIMARY KEY AUTOINCREMENT,
    Platform       TEXT NOT NULL,
    Caption        TEXT NOT NULL,
    MediaReference TEXT NOT NULL,
    ScheduledFor   TEXT NULL,
    Status         TEXT NOT NULL CHECK (Status IN
        ('PendingApproval','Approved','Rejected','ReadyToPublish','Published','Failed')),
    CreatedAt      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    ApprovedAt     TEXT NULL,
    PublishedAt    TEXT NULL,
    PlatformPostId TEXT NULL,
    ErrorMessage   TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_PostDrafts_Status ON PostDrafts (Status);
";

    private readonly string _connStr;
    private bool _initialized;

    public SqliteDraftRepository(string? conn)
    {
        _connStr = conn ?? "Data Source=data/iris.db";
    }

    public async Task InitAsync()
    {
        if (_initialized) return;
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = MigrationUp;
        await cmd.ExecuteNonQueryAsync();
        _initialized = true;
    }

    public async Task<PostDraft> CreateAsync(PostDraft draft)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO PostDrafts (Platform, Caption, MediaReference, ScheduledFor, Status, CreatedAt)
VALUES ($p, $c, $m, $sf, $st, $ca);
SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$p", draft.Platform);
        cmd.Parameters.AddWithValue("$c", draft.Caption);
        cmd.Parameters.AddWithValue("$m", draft.MediaReference);
        cmd.Parameters.AddWithValue("$sf", (object?)draft.ScheduledFor?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$st", draft.Status.ToString());
        cmd.Parameters.AddWithValue("$ca", draft.CreatedAt.ToString("O"));
        draft.Id = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        return draft;
    }

    public async Task<PostDraft?> GetAsync(long id)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{SelectColumns} WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? Map(r) : null;
    }

    public async Task<IReadOnlyList<PostDraft>> ListAsync(DraftStatus? status = null, int limit = 100)
    {
        var list = new List<PostDraft>();
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = status is null
            ? $"{SelectColumns} ORDER BY Id DESC LIMIT $l"
            : $"{SelectColumns} WHERE Status = $s ORDER BY Id DESC LIMIT $l";
        if (status is not null) cmd.Parameters.AddWithValue("$s", status.ToString());
        cmd.Parameters.AddWithValue("$l", limit);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(Map(r));
        return list;
    }

    public async Task<int> CountCreatedTodayAsync(string platform)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM PostDrafts WHERE Platform = $p AND CreatedAt >= $d";
        cmd.Parameters.AddWithValue("$p", platform);
        cmd.Parameters.AddWithValue("$d", DateTimeOffset.UtcNow.Date.ToString("O"));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<bool> TransitionAsync(long id, DraftStatus from, DraftStatus to,
        string? platformPostId = null, string? errorMessage = null)
    {
        if (!DraftStateMachine.CanTransition(from, to))
            throw new InvalidOperationException(
                $"Illegal draft transition {from} -> {to}. Drafts must pass through " +
                "PendingApproval -> Approved -> ReadyToPublish before they can be Published.");

        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        // Guarded UPDATE: the WHERE clause re-checks the expected source state, so a
        // concurrent transition (or any attempt to skip a state) affects zero rows.
        cmd.CommandText = @"
UPDATE PostDrafts SET
    Status         = $to,
    ApprovedAt     = CASE WHEN $to = 'Approved' THEN $now ELSE ApprovedAt END,
    PublishedAt    = CASE WHEN $to = 'Published' THEN $now ELSE PublishedAt END,
    PlatformPostId = COALESCE($ppid, PlatformPostId),
    ErrorMessage   = CASE WHEN $to = 'Failed' THEN $err
                          WHEN $to = 'ReadyToPublish' THEN NULL
                          ELSE ErrorMessage END
WHERE Id = $id AND Status = $from";
        cmd.Parameters.AddWithValue("$to", to.ToString());
        cmd.Parameters.AddWithValue("$from", from.ToString());
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$ppid", (object?)platformPostId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$err", (object?)errorMessage ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync() == 1;
    }

    private const string SelectColumns =
        "SELECT Id, Platform, Caption, MediaReference, ScheduledFor, Status, CreatedAt, ApprovedAt, PublishedAt, PlatformPostId, ErrorMessage FROM PostDrafts";

    private static PostDraft Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Platform = r.GetString(1),
        Caption = r.GetString(2),
        MediaReference = r.GetString(3),
        ScheduledFor = r.IsDBNull(4) ? null : DateTimeOffset.Parse(r.GetString(4)),
        Status = Enum.Parse<DraftStatus>(r.GetString(5)),
        CreatedAt = DateTimeOffset.Parse(r.GetString(6)),
        ApprovedAt = r.IsDBNull(7) ? null : DateTimeOffset.Parse(r.GetString(7)),
        PublishedAt = r.IsDBNull(8) ? null : DateTimeOffset.Parse(r.GetString(8)),
        PlatformPostId = r.IsDBNull(9) ? null : r.GetString(9),
        ErrorMessage = r.IsDBNull(10) ? null : r.GetString(10),
    };

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connStr);
        c.Open();
        return c;
    }
}
