-- housekeeping (architecture §9.4): removes rows that are short-lived by design. Its own login, so the deletes it
-- needs are granted to nothing else (grants/0005). Password from ServiceRoles__housekeeping_svc, like the others.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'housekeeping_svc') THEN
        CREATE ROLE housekeeping_svc LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT;
    END IF;
END $$;

COMMENT ON ROLE housekeeping_svc IS 'housekeeping: outbox cleanup, change-log archival, inbox retention';
