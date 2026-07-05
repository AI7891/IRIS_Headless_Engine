-- Migration 002 (up): PostDrafts approval queue.
-- Applied automatically at startup by SqliteDraftRepository.InitAsync (idempotent);
-- this file exists so the migration can also be applied/reviewed manually:
--   sqlite3 data/iris.db < scripts/migrations/002_post_drafts.up.sql

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
