#!/usr/bin/env python3
"""Demonstrate missed scalar/tuple tails in a disposable PostgreSQL 16 container.

See ../immich-watermark-source-research.md. Uses only Python's standard library
and Docker's psql client. This is a synthetic ordering proof, not an Immich test.
"""

import argparse
import json
import os
import selectors
import subprocess
import sys
import time
import uuid


LABEL = "org.immich-reversegeo.research"
PURPOSE = "watermark-commit-inversion"
LOW_ID = "00000000-0000-4000-8000-000000000001"
HIGH_ID = "00000000-0000-4000-8000-000000000002"


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def docker(*args, input_text=None):
    return subprocess.run(
        ["docker", *args], input=input_text, text=True, capture_output=True,
        check=True, timeout=20,
    ).stdout.strip()


def psql_args(container, database):
    return ["exec", "-i", container, "psql", "-X", "-qAt",
            "--set", "ON_ERROR_STOP=on", "--username", "research",
            "--dbname", database]


class ResearchSession:
    """A bounded command/acknowledgment channel to one real PostgreSQL session."""

    def __init__(self, container, database):
        self.process = subprocess.Popen(
            ["docker", *psql_args(container, database)], stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        )

    def execute(self, sql):
        marker = ("research_done_" + uuid.uuid4().hex).encode()
        self.process.stdin.write(sql.encode() + b"\n\\echo " + marker + b"\n")
        self.process.stdin.flush()
        output = b""
        deadline = time.monotonic() + 15
        with selectors.DefaultSelector() as selector:
            selector.register(self.process.stdout, selectors.EVENT_READ)
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0 or not selector.select(remaining):
                    raise TimeoutError("psql command acknowledgment timed out")
                chunk = os.read(self.process.stdout.fileno(), 65536)
                if not chunk:
                    raise RuntimeError("psql exited before acknowledgment: " + output.decode())
                output += chunk
                lines = output.splitlines()
                if lines and lines[-1] == marker and output.endswith(b"\n"):
                    return b"\n".join(lines[:-1]).decode().strip()

    def close(self):
        # Exiting psql rolls back any open transaction. A stuck child is reaped;
        # database cleanup subsequently terminates remaining database backends.
        try:
            if self.process.poll() is None:
                try:
                    self.process.communicate(b"\\q\n", timeout=5)
                except subprocess.TimeoutExpired:
                    self.process.kill()
                    self.process.communicate(timeout=5)
        finally:
            self.process.stdin.close()
            self.process.stdout.close()


def demonstrate(lower, higher, observer, equal_markers):
    observer.execute("TRUNCATE candidate;")
    lower_stamp = "2026-01-01T00:00:00.000Z"
    higher_stamp = lower_stamp if equal_markers else "2026-01-01T00:00:00.001Z"
    lower_uuid = "019b76da-a800-7000-8000-000000000001"
    higher_uuid = lower_uuid if equal_markers else "019b76da-a801-7000-8000-000000000002"

    def insert_sql(row_id, stamp, marker):
        return f"""BEGIN;
            INSERT INTO candidate VALUES ('{row_id}', '{stamp}', '{marker}',
                pg_current_xact_id()::text::bigint, true);
            SELECT assigned_xid FROM candidate WHERE id = '{row_id}';"""

    # The acknowledgment comes after INSERT and SELECT on that same session.
    low_xid = int(lower.execute(insert_sql(LOW_ID, lower_stamp, lower_uuid)))
    high_xid = int(higher.execute(insert_sql(HIGH_ID, higher_stamp, higher_uuid)))
    require(low_xid < high_xid, "T1 must receive the lower real transaction ID")
    higher.execute("COMMIT;")
    before = json.loads(observer.execute(
        "SELECT json_agg(id ORDER BY id) FROM candidate WHERE eligible;"))
    require(before == [HIGH_ID], "Poll must see T2 and must not see uncommitted T1")
    print(json.dumps({"phase": "higher-committed-lower-held", "equalMarkers": equal_markers,
                      "visible": before, "lowerXid": low_xid, "higherXid": high_xid}), flush=True)
    # Positive controls prove the same tuple predicates can discover visible T2.
    # Equal timestamp/UUID values are distinguished here by the stable asset ID.
    for column, value in (
        ("stamp", f"'{lower_stamp}'::timestamptz"),
        ("marker", f"'{lower_uuid}'::uuid"),
        ("assigned_xid", str(low_xid)), ("xmin::text::bigint", str(low_xid)),
    ):
        found = observer.execute(f"SELECT EXISTS (SELECT 1 FROM candidate WHERE eligible "
                                 f"AND ({column}, id) > ({value}, '{LOW_ID}'::uuid));")
        require(found == "t", f"Positive control for {column} tuple must find visible T2")
    # Persist only an in-memory research watermark; consume the visible row.
    observer.execute(f"UPDATE candidate SET eligible = false WHERE id = '{HIGH_ID}';")
    lower.execute("COMMIT;")
    after = json.loads(observer.execute(
        "SELECT json_agg(id ORDER BY id) FROM candidate WHERE eligible;"))
    require(after == [LOW_ID], "Full current-state observation must find late-committed T1")
    bounds = {
        "timestamp": ("stamp", f"'{higher_stamp}'::timestamptz"),
        "uuid": ("marker", f"'{higher_uuid}'::uuid"),
        "xid": ("assigned_xid", str(high_xid)),
        "xmin": ("xmin::text::bigint", str(high_xid)),
    }
    tails = {}
    for name, (column, value) in bounds.items():
        for tuple_order in (False, True):
            predicate = (f"({column}, id) > ({value}, '{HIGH_ID}'::uuid)"
                         if tuple_order else f"{column} > {value}")
            found = observer.execute(
                f"SELECT EXISTS (SELECT 1 FROM candidate WHERE eligible AND {predicate});")
            require(found == "f", f"{name} tuple={tuple_order} must miss late-committed T1")
            tails[name + ("+id" if tuple_order else "")] = False
    return {"case": "equal-markers" if equal_markers else "increasing-markers",
            "fullEligibleIds": after, "tailsFindEligible": tails,
            "positiveTupleControls": 4,
            "lowerXid": low_xid, "higherXid": high_xid}


def run(container):
    info = json.loads(docker("inspect", container))[0]
    require(info["State"]["Running"], "Research container must be running")
    require((info["Config"].get("Labels") or {}).get(LABEL) == PURPOSE,
            "Container must carry the dedicated research label")
    require(info["HostConfig"]["NetworkMode"] == "none", "Research network must be none")
    require(not info["HostConfig"].get("PortBindings"), "Research container must have no host ports")
    require(not any(m["Type"] == "bind" for m in info["Mounts"]), "No host bind mounts allowed")
    # Use the inspected immutable ID for all subsequent operations.
    container = info["Id"]
    version = docker(*psql_args(container, "postgres"), input_text="SHOW server_version_num;")
    require(160000 <= int(version) < 170000, "This proof is pinned to PostgreSQL 16")
    database = "wm_research_" + uuid.uuid4().hex
    sessions = []
    primary_error = None
    created = False
    try:
        # Mark before attempting CREATE: even a client-side timeout may have created it.
        created = True
        docker(*psql_args(container, "postgres"), input_text=f"CREATE DATABASE {database};")
        for _ in range(3):
            session = ResearchSession(container, database)
            sessions.append(session)
            session.execute("SET statement_timeout = '10s'; SET lock_timeout = '5s'; SET timezone = 'UTC';")
        lower, higher, observer = sessions
        observer.execute("""CREATE TABLE candidate (
            id uuid PRIMARY KEY, stamp timestamptz NOT NULL, marker uuid NOT NULL,
            assigned_xid bigint NOT NULL, eligible boolean NOT NULL);""")
        results = [demonstrate(lower, higher, observer, equal) for equal in (False, True)]
        print(json.dumps({"postgresVersionNum": version, "database": database,
                          "results": results, "proof": "scalar-and-tuple-commit-inversion"}), flush=True)
    except BaseException as error:
        primary_error = error
        raise
    finally:
        cleanup_errors = []
        for session in sessions:
            try:
                session.close()
            except Exception as error:
                cleanup_errors.append(str(error))
        if created:
            try:
                docker(*psql_args(container, "postgres"),
                       input_text=f"DROP DATABASE IF EXISTS {database} WITH (FORCE);")
                absent = docker(*psql_args(container, "postgres"), input_text=
                                f"SELECT NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = '{database}');")
                require(absent == "t", "Owned research database must be removed")
                print(json.dumps({"cleanup": "database-absent", "database": database}), flush=True)
            except Exception as error:
                cleanup_errors.append(str(error))
        if cleanup_errors:
            message = "Research cleanup failed: " + "; ".join(cleanup_errors)
            if primary_error is not None:
                print(message, file=sys.stderr)
            else:
                raise RuntimeError(message)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("container", help="Dedicated, labeled PostgreSQL 16 research container")
    run(parser.parse_args().container)
