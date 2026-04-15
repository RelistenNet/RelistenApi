"""
Fetch all artists from the Relisten production database and save locally.

Uses the production read-only Postgres connection. Requires kubectl access
to fetch the database password.

Usage:
    python3 fetch_relisten_artists.py
"""

import json
import subprocess
import time
from pathlib import Path

DATA_DIR = Path(__file__).parent / "data"

DB_HOST = "relisten2.tail09dbf.ts.net"
DB_PORT = "32095"
DB_USER = "app"
DB_NAME = "app"


def get_db_password() -> str:
    """Fetch the production read-only database password from kubectl."""
    result = subprocess.run(
        [
            "kubectl",
            "-n",
            "default",
            "get",
            "secret",
            "relisten-db-app",
            "-o",
            "jsonpath={.data.password}",
        ],
        capture_output=True,
        text=True,
        check=True,
    )
    import base64

    return base64.b64decode(result.stdout).decode("utf-8")


def fetch_artists(password: str) -> list[dict]:
    """Fetch all artists with their upstream identifiers and features."""
    import psycopg2  # type: ignore

    conn = psycopg2.connect(
        host=DB_HOST,
        port=DB_PORT,
        user=DB_USER,
        password=password,
        dbname=DB_NAME,
    )

    try:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT
                    a.id,
                    a.uuid,
                    a.name,
                    a.slug,
                    a.sort_name,
                    a.featured,
                    aus.upstream_identifier,
                    aus.upstream_source_id,
                    (SELECT count(*) FROM shows s WHERE s.artist_id = a.id) as show_count,
                    (SELECT count(*) FROM sources s WHERE s.artist_id = a.id) as source_count
                FROM artists a
                LEFT JOIN artists_upstream_sources aus
                    ON aus.artist_id = a.id AND aus.upstream_source_id = 1
                ORDER BY a.name
                """
            )

            columns = [desc[0] for desc in cur.description]
            return [dict(zip(columns, row)) for row in cur.fetchall()]
    finally:
        conn.close()


def main():
    DATA_DIR.mkdir(exist_ok=True)

    print("Fetching database password...")
    password = get_db_password()

    print("Fetching artists from production...")

    try:
        artists = fetch_artists(password)
    except ImportError:
        print("\npsycopg2 not installed. Falling back to psql + JSON export...")
        artists = fetch_artists_via_psql(password)

    output_path = DATA_DIR / "relisten_artists.json"
    with open(output_path, "w") as f:
        json.dump(
            {
                "fetched_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                "total_artists": len(artists),
                "artists": artists,
            },
            f,
            indent=2,
            default=str,
        )

    print(f"Saved {len(artists)} artists to {output_path}")

    archive_artists = [a for a in artists if a.get("upstream_identifier")]
    print(f"  {len(archive_artists)} have archive.org upstream identifiers")
    print(f"  {len(artists) - len(archive_artists)} have no archive.org mapping")


def fetch_artists_via_psql(password: str) -> list[dict]:
    """Fallback: use psql to export artists as JSON if psycopg2 isn't available."""
    import os

    env = os.environ.copy()
    env["PGPASSWORD"] = password

    query = """
    SELECT json_agg(row_to_json(t))
    FROM (
        SELECT
            a.id,
            a.uuid::text as uuid,
            a.name,
            a.slug,
            a.sort_name,
            a.featured,
            aus.upstream_identifier,
            aus.upstream_source_id,
            (SELECT count(*) FROM shows s WHERE s.artist_id = a.id) as show_count,
            (SELECT count(*) FROM sources s WHERE s.artist_id = a.id) as source_count
        FROM artists a
        LEFT JOIN artists_upstream_sources aus
            ON aus.artist_id = a.id AND aus.upstream_source_id = 1
        ORDER BY a.name
    ) t;
    """

    result = subprocess.run(
        [
            "psql",
            "-h",
            DB_HOST,
            "-p",
            DB_PORT,
            "-U",
            DB_USER,
            "-d",
            DB_NAME,
            "-t",
            "-A",
            "-c",
            query,
        ],
        capture_output=True,
        text=True,
        env=env,
        check=True,
    )

    return json.loads(result.stdout.strip())


if __name__ == "__main__":
    main()
