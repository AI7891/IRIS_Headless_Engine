-- Migration 002 (down): rollback of the PostDrafts approval queue.
--   sqlite3 data/iris.db < scripts/migrations/002_post_drafts.down.sql
-- WARNING: drops the drafts table and all approval history.

DROP INDEX IF EXISTS IX_PostDrafts_Status;
DROP TABLE IF EXISTS PostDrafts;
