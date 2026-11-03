-- Secure Intake schema 0001 (PostgreSQL). Runs as the schema owner. The runtime role
-- (secureintake_app) must not own tables or hold BYPASSRLS (doc 03 §3). Times are Unix milliseconds.

-- ── Control plane ─────────────────────────────────────────────────────────
CREATE TABLE deployment_state (
    key   text PRIMARY KEY,
    value text NOT NULL
);
INSERT INTO deployment_state (key, value) VALUES ('recovery_epoch', '1');

CREATE TABLE organisations (
    id           uuid PRIMARY KEY,
    slug         text NOT NULL UNIQUE,
    display_name text NOT NULL,
    status       text NOT NULL,
    method       text NOT NULL,
    version      bigint NOT NULL,
    created_at   bigint NOT NULL,
    updated_at   bigint NOT NULL
);

CREATE TABLE organisation_resource_assignments (
    organisation_id       uuid PRIMARY KEY REFERENCES organisations (id),
    database_profile      text NOT NULL,
    storage_profile       text NOT NULL,
    assignment_generation bigint NOT NULL,
    writes_blocked        integer NOT NULL DEFAULT 0,
    updated_at            bigint NOT NULL
);

CREATE TABLE service_accounts (
    id              uuid PRIMARY KEY,
    name            text NOT NULL,
    scope           text NOT NULL CHECK (scope IN ('platform', 'organisation')),
    organisation_id uuid REFERENCES organisations (id),
    roles           text NOT NULL,
    permissions     text NOT NULL,
    token_digest    bytea NOT NULL UNIQUE,
    status          text NOT NULL,
    created_at      bigint NOT NULL,
    last_used_at    bigint
);

CREATE TABLE audit_heads (
    scope text PRIMARY KEY,
    seq   bigint NOT NULL,
    hash  bytea NOT NULL
);

CREATE TABLE platform_audit (
    seq         bigint PRIMARY KEY,
    time        bigint NOT NULL,
    event       text NOT NULL,
    record_json text NOT NULL,
    prev_hash   bytea NOT NULL,
    hash        bytea NOT NULL
);

CREATE TABLE audit_checkpoints (
    scope          text NOT NULL,
    seq            bigint NOT NULL,
    hash           bytea NOT NULL,
    recovery_epoch bigint NOT NULL,
    time           bigint NOT NULL,
    key_id         text NOT NULL,
    signature      bytea NOT NULL,
    PRIMARY KEY (scope, seq)
);

CREATE TABLE outbox_events (
    id              uuid PRIMARY KEY,
    organisation_id uuid,
    type            text NOT NULL,
    version         integer NOT NULL,
    payload         text NOT NULL,
    created_at      bigint NOT NULL,
    dispatched_at   bigint
);
CREATE INDEX ix_outbox_pending ON outbox_events (created_at) WHERE dispatched_at IS NULL;

CREATE TABLE jobs (
    id               uuid PRIMARY KEY,
    organisation_id  uuid,
    class            text NOT NULL,
    type             text NOT NULL,
    payload_version  integer NOT NULL,
    payload          text NOT NULL,
    state            text NOT NULL,
    priority_class   text NOT NULL,
    attempts         integer NOT NULL DEFAULT 0,
    max_attempts     integer NOT NULL,
    not_before       bigint NOT NULL,
    lease_owner      text,
    lease_expires_at bigint,
    idempotency_key  text UNIQUE,
    generation       bigint,
    last_error       text,
    created_at       bigint NOT NULL,
    updated_at       bigint NOT NULL
);
CREATE INDEX ix_jobs_claim ON jobs (state, priority_class, not_before);

-- ── Tenant data plane (RLS) ───────────────────────────────────────────────
CREATE TABLE dropboxes (
    id              uuid PRIMARY KEY,
    organisation_id uuid NOT NULL,
    name            text NOT NULL,
    status          text NOT NULL,
    version         bigint NOT NULL,
    created_at      bigint NOT NULL,
    updated_at      bigint NOT NULL
);
CREATE INDEX ix_dropboxes_org ON dropboxes (organisation_id);

CREATE TABLE organisation_audit_heads (
    organisation_id uuid PRIMARY KEY,
    seq             bigint NOT NULL,
    hash            bytea NOT NULL
);

CREATE TABLE organisation_audit (
    organisation_id uuid NOT NULL,
    seq             bigint NOT NULL,
    time            bigint NOT NULL,
    event           text NOT NULL,
    record_json     text NOT NULL,
    prev_hash       bytea NOT NULL,
    hash            bytea NOT NULL,
    PRIMARY KEY (organisation_id, seq)
);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['dropboxes', 'organisation_audit_heads', 'organisation_audit'] LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format($p$CREATE POLICY %I ON %I
            USING (organisation_id = NULLIF(current_setting('app.organisation_id', true), '')::uuid)
            WITH CHECK (organisation_id = NULLIF(current_setting('app.organisation_id', true), '')::uuid)$p$,
            t || '_tenant_isolation', t);
    END LOOP;
END $$;

-- Runtime role grants. Audit tables are append-only for the runtime role.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'secureintake_app') THEN
        GRANT SELECT, INSERT, UPDATE ON deployment_state, organisations, organisation_resource_assignments,
            service_accounts, audit_heads, outbox_events, jobs, dropboxes, organisation_audit_heads TO secureintake_app;
        GRANT SELECT, INSERT ON platform_audit, organisation_audit, audit_checkpoints TO secureintake_app;
        GRANT SELECT ON schema_migrations TO secureintake_app;
    END IF;
END $$;
