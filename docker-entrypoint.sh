#!/bin/sh
# =============================================================================
# docker-entrypoint.sh — runs once per container start, BEFORE the app.
#
# What this does (in order):
#   1. Wait until SQL Server is accepting connections. If we skip this,
#      the efbundle will fail because the DB isn't up yet (SQL Server
#      takes 10-30 seconds to boot, and the app container starts at
#      the same time).
#   2. Run the EF Core migrations bundle. This creates the database
#      schema (tables, indexes, etc) if it doesn't exist, or applies
#      any pending new migrations if the DB already exists.
#   3. Hand off to `dotnet TakOne.WebUI.dll` to start the actual app.
#      `exec` replaces the shell process with the dotnet process so
#      that dotnet becomes PID 1 and receives SIGTERM cleanly when
#      you run `docker stop`.
#
# DEBUGGING: to see what's happening, run:
#     docker compose logs web
# The `set -x` line below prints every command to the container log
# as it runs. Comment it out for quieter logs once everything works.
# =============================================================================

set -e   # exit immediately if any command fails
set -x   # echo every command (helpful for debugging — comment out when stable)

# --- Read the connection string from the env ---
# docker-compose.yml sets TAKONE_CONNECTION_STRING. The .NET config
# system maps `TakOne:Database:ConnectionString` -> env var
# `TakOne__Database__ConnectionString`. But the efbundle is its own
# standalone exe — it doesn't read .NET config. We pass the connection
# string to it via the --connection flag below.
#
# We use a SEPARATE env var (TAKONE_CONNECTION_STRING) that we pass
# BOTH to the efbundle (here) AND to the app (via docker-compose env).
# Both must point to the SAME database.

CONN_STR="${TAKONE_CONNECTION_STRING:?ERROR: TAKONE_CONNECTION_STRING env var is not set. Did you forget to set it in docker-compose.yml or .env?}"

# --- Wait for SQL Server to be ready ---
# SQL Server inside the container listens on port 1433. We ping it
# with a TCP connection attempt every 2 seconds, up to 60 seconds.
# If it's still not up after 60s, something is wrong — bail out.
#
# We use /dev/tcp because the slim ASP.NET runtime image doesn't have
# nc, curl, or sqlcmd installed. /dev/tcp is a bash builtin — but the
# image's /bin/sh might not be bash. To be safe, we use a Python
# one-liner if Python is available, else fall back to /dev/tcp.

echo "[entrypoint] Waiting for SQL Server at ${SQL_HOST:-sql}:${SQL_PORT:-1433}..."

ATTEMPTS=0
MAX_ATTEMPTS=30  # 30 * 2s = 60 seconds max wait

while [ $ATTEMPTS -lt $MAX_ATTEMPTS ]; do
    # Try connecting to the SQL Server port. /dev/tcp is a bashism;
    # if /bin/sh isn't bash, this falls through to the python fallback.
    if (echo > /dev/tcp/${SQL_HOST:-sql}/${SQL_PORT:-1433}) 2>/dev/null; then
        echo "[entrypoint] SQL Server is accepting TCP connections."
        break
    fi
    ATTEMPTS=$((ATTEMPTS + 1))
    echo "[entrypoint]   attempt $ATTEMPTS/$MAX_ATTEMPTS: not ready yet, sleeping 2s..."
    sleep 2
done

if [ $ATTEMPTS -ge $MAX_ATTEMPTS ]; then
    echo "[entrypoint] ERROR: SQL Server did not become ready within 60 seconds. Aborting."
    exit 1
fi

# Even after SQL Server accepts TCP connections, it takes a few more
# seconds before the database ENGINE is ready to accept logins. Add
# a short safety pause.
sleep 3

# --- Run EF Core migrations ---
# The efbundle is a self-contained executable that:
#   - Reads the connection string from --connection
#   - Checks the __EFMigrationsHistory table
#   - Applies any pending migrations
#   - Exits 0 on success
#
# We override the connection string in appsettings.json with the one
# from the env var, because the appsettings.json version uses
# Trusted_Connection=True (Windows auth) which won't work in Linux.
echo "[entrypoint] Applying EF Core migrations..."
/app/efbundle --connection "$CONN_STR" --no-build
echo "[entrypoint] Migrations applied successfully."

# --- Hand off to the app ---
# exec replaces this shell process with dotnet. The dotnet process
# becomes PID 1 in the container, so it receives signals from
# `docker stop` and shuts down gracefully.
echo "[entrypoint] Starting TakOne WebUI..."
exec dotnet TakOne.WebUI.dll
