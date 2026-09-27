DO $$
BEGIN
    EXECUTE format('REVOKE ALL ON DATABASE %I FROM PUBLIC', current_database());
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO ledger_runtime', current_database());
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO ledger_migrator', current_database());
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO grafana_reader', current_database());
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO ledger_backup', current_database());
END
$$;

ALTER SCHEMA public OWNER TO ledger_migrator;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO ledger_runtime;

ALTER DEFAULT PRIVILEGES FOR ROLE ledger_migrator IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ledger_runtime;

ALTER DEFAULT PRIVILEGES FOR ROLE ledger_migrator IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO ledger_runtime;
