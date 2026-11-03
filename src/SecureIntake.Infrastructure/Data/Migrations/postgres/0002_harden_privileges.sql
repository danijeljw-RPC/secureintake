-- 0002 (PostgreSQL). The runtime role must not be able to modify the schema (ADR-0007).
-- PostgreSQL 14 and earlier grant CREATE on schema public to PUBLIC; remove it on every version.
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'secureintake_app') THEN
        REVOKE CREATE ON SCHEMA public FROM secureintake_app;
        GRANT USAGE ON SCHEMA public TO secureintake_app;
        GRANT SELECT ON schema_migration_log TO secureintake_app;
    END IF;
END $$;
