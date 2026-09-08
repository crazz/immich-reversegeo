\set ON_ERROR_STOP on

CREATE TABLE asset (
    id uuid PRIMARY KEY,
    "createdAt" timestamptz NOT NULL,
    "deletedAt" timestamptz NULL
);

CREATE TABLE asset_exif (
    "assetId" uuid PRIMARY KEY REFERENCES asset(id) ON DELETE CASCADE,
    latitude double precision NULL,
    longitude double precision NULL,
    city text NULL,
    state text NULL,
    country text NULL
);

CREATE TABLE smoke_control (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    worker_role name NOT NULL,
    hold_worker boolean NOT NULL DEFAULT false
);

INSERT INTO smoke_control(singleton, worker_role, hold_worker)
VALUES (true, :'standard_role', false);

CREATE ROLE :"standard_role" LOGIN PASSWORD :'standard_password'
    NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT;
CREATE ROLE :"webonly_role" LOGIN PASSWORD :'webonly_password'
    NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT;
CREATE ROLE :"runonce_role" LOGIN PASSWORD :'runonce_password'
    NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT;

ALTER ROLE :"standard_role" SET application_name TO :'standard_application_name';
ALTER ROLE :"standard_role" SET lock_timeout TO '20s';
ALTER ROLE :"webonly_role" SET application_name TO :'webonly_application_name';
ALTER ROLE :"runonce_role" SET application_name TO :'runonce_application_name';

GRANT CONNECT ON DATABASE :"database_name" TO :"standard_role", :"webonly_role", :"runonce_role";
GRANT USAGE ON SCHEMA public TO :"standard_role", :"webonly_role", :"runonce_role";
GRANT SELECT ON asset, asset_exif TO :"standard_role", :"webonly_role", :"runonce_role";
GRANT UPDATE (city, state, country) ON asset_exif TO :"standard_role", :"runonce_role";

CREATE FUNCTION smoke_gate_worker_count()
RETURNS boolean
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $$
DECLARE
    must_hold boolean;
BEGIN
    SELECT control.hold_worker
      INTO must_hold
      FROM public.smoke_control AS control
     WHERE control.singleton
       AND control.worker_role = session_user;

    IF COALESCE(must_hold, false)
       AND EXISTS (
           SELECT 1
             FROM pg_catalog.pg_locks AS production_lock
             JOIN pg_catalog.pg_stat_activity AS activity
               ON activity.pid = production_lock.pid
            WHERE production_lock.locktype = 'advisory'
              AND production_lock.database = (
                  SELECT oid FROM pg_catalog.pg_database WHERE datname = current_database())
              AND production_lock.classid = 2439209123::oid
              AND production_lock.objid = 4161460176::oid
              AND production_lock.objsubid = 1
              AND production_lock.granted
              AND production_lock.pid <> pg_backend_pid()
              AND activity.usename = session_user)
    THEN
        PERFORM pg_catalog.pg_advisory_lock(460046);
    END IF;

    RETURN true;
END;
$$;

REVOKE ALL ON FUNCTION smoke_gate_worker_count() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION smoke_gate_worker_count() TO :"standard_role";

ALTER TABLE asset ENABLE ROW LEVEL SECURITY;
ALTER TABLE asset FORCE ROW LEVEL SECURITY;
CREATE POLICY smoke_asset_standard_select ON asset
    FOR SELECT TO :"standard_role"
    USING (smoke_gate_worker_count());
CREATE POLICY smoke_asset_runonce_select ON asset
    FOR SELECT TO :"runonce_role"
    USING (true);

SELECT CASE WHEN rolname = :'standard_role'
                 AND NOT rolsuper
                 AND NOT rolcreaterole
                 AND NOT rolcreatedb
            THEN true ELSE false END AS standard_role_is_scoped
  FROM pg_catalog.pg_roles
 WHERE rolname = :'standard_role';

SELECT relrowsecurity, relforcerowsecurity
  FROM pg_catalog.pg_class
 WHERE oid = 'public.asset'::regclass;
