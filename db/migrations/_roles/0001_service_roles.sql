-- One database login per service (architecture §9.4, §12, plan phase 9). Applied right after _bootstrap, so every
-- later migration can grant to these roles in the same file that creates a table.
--
-- No passwords here: migration files are committed. The db-migrator sets each password from the environment
-- (ServiceRoles__<role>), and a role without one cannot log in at all. Roles are cluster-wide, hence IF NOT EXISTS.
-- Privileges are granted in grants/0001, after every table exists. The table owner (the migrator's login) is used
-- by nothing but the migrator.
DO $$
DECLARE
    r text;
BEGIN
    FOREACH r IN ARRAY ARRAY['identity_svc', 'gateway_svc', 'persister_svc', 'relay_svc', 'orchestrator_svc', 'rag_svc']
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
            EXECUTE format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT', r);
        END IF;
    END LOOP;
END $$;

COMMENT ON ROLE identity_svc IS 'identity-service: clinicians, credentials, sessions, devices, auth audit';
COMMENT ON ROLE gateway_svc IS 'sync-gateway: push/pull, patient alias, REST baseline';
COMMENT ON ROLE persister_svc IS 'ingest-persister: wound-events -> clinical record';
COMMENT ON ROLE relay_svc IS 'outbox-relay: messaging.outbox -> Kafka';
COMMENT ON ROLE orchestrator_svc IS 'orchestrator: recommendations';
COMMENT ON ROLE rag_svc IS 'Recommendation Service (Member 3): rag schema only';
