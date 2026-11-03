-- Secure Intake schema 0001 (SQLite, standalone). Control plane and tenant data plane share one file
-- but stay logically separate (doc 03 §3). Times are Unix milliseconds; IDs are UUIDv7 text.

-- ── Control plane ─────────────────────────────────────────────────────────
CREATE TABLE deployment_state (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
INSERT INTO deployment_state (key, value) VALUES ('recovery_epoch', '1');

CREATE TABLE organisations (
    id           TEXT PRIMARY KEY,
    slug         TEXT NOT NULL UNIQUE,
    display_name TEXT NOT NULL,
    status       TEXT NOT NULL,
    method       TEXT NOT NULL,
    version      INTEGER NOT NULL,
    created_at   INTEGER NOT NULL,
    updated_at   INTEGER NOT NULL
);

CREATE TABLE organisation_resource_assignments (
    organisation_id       TEXT PRIMARY KEY REFERENCES organisations (id),
    database_profile      TEXT NOT NULL,
    storage_profile       TEXT NOT NULL,
    assignment_generation INTEGER NOT NULL,
    writes_blocked        INTEGER NOT NULL DEFAULT 0,
    updated_at            INTEGER NOT NULL
);

CREATE TABLE service_accounts (
    id              TEXT PRIMARY KEY,
    name            TEXT NOT NULL,
    scope           TEXT NOT NULL CHECK (scope IN ('platform', 'organisation')),
    organisation_id TEXT REFERENCES organisations (id),
    roles           TEXT NOT NULL,
    permissions     TEXT NOT NULL,
    token_digest    BLOB NOT NULL UNIQUE,
    status          TEXT NOT NULL,
    created_at      INTEGER NOT NULL,
    last_used_at    INTEGER
);

CREATE TABLE audit_heads (
    scope TEXT PRIMARY KEY,
    seq   INTEGER NOT NULL,
    hash  BLOB NOT NULL
);

CREATE TABLE platform_audit (
    seq         INTEGER PRIMARY KEY,
    time        INTEGER NOT NULL,
    event       TEXT NOT NULL,
    record_json TEXT NOT NULL,
    prev_hash   BLOB NOT NULL,
    hash        BLOB NOT NULL
);

CREATE TABLE audit_checkpoints (
    scope          TEXT NOT NULL,
    seq            INTEGER NOT NULL,
    hash           BLOB NOT NULL,
    recovery_epoch INTEGER NOT NULL,
    time           INTEGER NOT NULL,
    key_id         TEXT NOT NULL,
    signature      BLOB NOT NULL,
    PRIMARY KEY (scope, seq)
);

CREATE TABLE outbox_events (
    id              TEXT PRIMARY KEY,
    organisation_id TEXT,
    type            TEXT NOT NULL,
    version         INTEGER NOT NULL,
    payload         TEXT NOT NULL,
    created_at      INTEGER NOT NULL,
    dispatched_at   INTEGER
);
CREATE INDEX ix_outbox_pending ON outbox_events (created_at) WHERE dispatched_at IS NULL;

CREATE TABLE jobs (
    id               TEXT PRIMARY KEY,
    organisation_id  TEXT,
    class            TEXT NOT NULL,
    type             TEXT NOT NULL,
    payload_version  INTEGER NOT NULL,
    payload          TEXT NOT NULL,
    state            TEXT NOT NULL,
    priority_class   TEXT NOT NULL,
    attempts         INTEGER NOT NULL DEFAULT 0,
    max_attempts     INTEGER NOT NULL,
    not_before       INTEGER NOT NULL,
    lease_owner      TEXT,
    lease_expires_at INTEGER,
    idempotency_key  TEXT UNIQUE,
    generation       INTEGER,
    last_error       TEXT,
    created_at       INTEGER NOT NULL,
    updated_at       INTEGER NOT NULL
);
CREATE INDEX ix_jobs_claim ON jobs (state, priority_class, not_before);

-- ── Tenant data plane ─────────────────────────────────────────────────────
CREATE TABLE dropboxes (
    id              TEXT PRIMARY KEY,
    organisation_id TEXT NOT NULL,
    name            TEXT NOT NULL,
    status          TEXT NOT NULL,
    version         INTEGER NOT NULL,
    created_at      INTEGER NOT NULL,
    updated_at      INTEGER NOT NULL
);
CREATE INDEX ix_dropboxes_org ON dropboxes (organisation_id);

CREATE TABLE organisation_audit_heads (
    organisation_id TEXT PRIMARY KEY,
    seq             INTEGER NOT NULL,
    hash            BLOB NOT NULL
);

CREATE TABLE organisation_audit (
    organisation_id TEXT NOT NULL,
    seq             INTEGER NOT NULL,
    time            INTEGER NOT NULL,
    event           TEXT NOT NULL,
    record_json     TEXT NOT NULL,
    prev_hash       BLOB NOT NULL,
    hash            BLOB NOT NULL,
    PRIMARY KEY (organisation_id, seq)
);

-- Append-only enforcement for audit tables.
CREATE TRIGGER platform_audit_no_update BEFORE UPDATE ON platform_audit BEGIN SELECT RAISE(ABORT, 'audit is append-only'); END;
CREATE TRIGGER platform_audit_no_delete BEFORE DELETE ON platform_audit BEGIN SELECT RAISE(ABORT, 'audit is append-only'); END;
CREATE TRIGGER organisation_audit_no_update BEFORE UPDATE ON organisation_audit BEGIN SELECT RAISE(ABORT, 'audit is append-only'); END;
CREATE TRIGGER organisation_audit_no_delete BEFORE DELETE ON organisation_audit BEGIN SELECT RAISE(ABORT, 'audit is append-only'); END;
