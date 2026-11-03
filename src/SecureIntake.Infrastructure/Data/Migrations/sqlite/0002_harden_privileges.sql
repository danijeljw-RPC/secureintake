-- 0002 (SQLite). PostgreSQL revokes schema-creation privileges in its 0002. SQLite has no roles, so this
-- migration only keeps version numbers aligned across dialects. The equivalent control for SQLite is file
-- ownership and the host CLI operator allowlist (ADR-0004, ADR-0007).
SELECT 1;
