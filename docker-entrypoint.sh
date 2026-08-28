#!/bin/sh
# =============================================================================
# docker-entrypoint.sh — runs once per container start, BEFORE the app.
#
# Sequence:
#   1. Wait until SQL Server is accepting TCP connections (it takes 10–30s
#      to boot; the web container starts at the same time, so we race).
#   2. Run the EF Core migrations bundle — creates the DB schema (tables,
#      indexes, etc.) if it doesn't exist, or applies pending migrations
#      if the DB already exists.
#   3. Hand off to `dotnet TakOne.WebUI.dll` via `exec` so dotnet becomes
#      PID 1 and receives SIGTERM cleanly when you `docker stop`.
#
# DEBUGGING: to echo every shell command to stdout (like the old `set -x`
# default), set ENTRYPOINT_TRACE=1 in the container environment. This is
# OFF by default — `set -x` was previously unconditionally on, which
# echoed the SQL Server SA connection string (including the password) to
# stdout on every container start. That output lands in `docker compose
# logs takone-web` and any log aggregator (Seq, ELK, Datadog). See
# Brutal Code Review v3 finding #05.
# =============================================================================
set -e   # exit immediately if any command fails

# Trace mode is opt-in via env var (default: off). Never echo the
# connection string (which contains the SA password) in normal operation.
if [ "${ENTRYPOINT_TRACE:-0}" = "1" ]; then
    set -x
fi

# ── Read connection string from env ──
# docker-compose.yml sets TAKONE_CONNECTION_STRING. The .NET config system
# maps `TakOne:Database:ConnectionString` → env var
# `TakOne__Database__ConnectionString`. But the efbundle is its own
# standalone exe — it doesn't read .NET config. We pass the connection
# string to it via the --connection flag below.
CONN_STR="${TAKONE_CONNECTION_STRING:?ERROR: TAKONE_CONNECTION_STRING env var is not set. Did you forget to set it in docker-compose.yml?}"

# ── Wait for SQL Server to be ready ──
# docker compose's `depends_on: condition: service_healthy` already
# guarantees SQL Server is fully ready (its healthcheck runs
# `sqlcmd SELECT 1`) before this container starts. This loop is
# pure defense-in-depth — for cases where someone runs the entrypoint
# without compose (e.g. `docker run` directly).
#
# We use `nc -z -w 2` (netcat, zero-I/O mode, 2s connect timeout) to
# TCP-probe the SQL Server port. The previous version used `/dev/tcp/...`
# but that's a bash-only builtin; /bin/sh on the aspnet:10.0 image is
# dash, so the check always returned false and the wait loop never broke.
# netcat-openbsd is installed in the Dockerfile.
echo "[entrypoint] TakOne WebUI v1.0.0 — waiting for SQL Server at ${SQL_HOST:-takone-db}:${SQL_PORT:-1433}..."

ATTEMPTS=0
MAX_ATTEMPTS=15   # 15 * 2s = 30 seconds max wait (compose already gated us)

while [ $ATTEMPTS -lt $MAX_ATTEMPTS ]; do
    if nc -z -w 2 "${SQL_HOST:-takone-db}" "${SQL_PORT:-1433}" 2>/dev/null; then
        echo "[entrypoint] SQL Server is accepting TCP connections."
        break
    fi
    ATTEMPTS=$((ATTEMPTS + 1))
    echo "[entrypoint]   attempt $ATTEMPTS/$MAX_ATTEMPTS: not ready yet, sleeping 2s..."
    sleep 2
done

if [ $ATTEMPTS -ge $MAX_ATTEMPTS ]; then
    echo "[entrypoint] ERROR: SQL Server did not become ready within 30 seconds. Aborting."
    echo "[entrypoint] (If running via docker compose, this should never happen — depends_on:"
    echo "[entrypoint]  condition: service_healthy already gates this container's start.)"
    exit 1
fi

# Even after SQL Server accepts TCP connections, it takes a few more seconds
# before the database ENGINE is ready to accept logins. Short safety pause.
sleep 3

# ── Run EF Core migrations ──
# The efbundle is a self-contained executable that:
#   - Reads the connection string from --connection
#   - Checks the __EFMigrationsHistory table
#   - Applies any pending migrations (creates the DB if it doesn't exist)
#   - Exits 0 on success
#
# NOTE: `--no-build` is a BUILD-TIME option for `dotnet ef migrations bundle`
# (used in the Dockerfile). The runtime `efbundle` executable does NOT
# recognize it — passing it here causes "Unrecognized option '--no-build'"
# and the bundle exits non-zero, which makes the container restart forever.
# The runtime options supported by efbundle are:
#   --connection, --context, --verbose, --no-color, --prefix-output,
#   --working-directory, --help
echo "[entrypoint] Applying EF Core migrations..."
/app/efbundle --connection "$CONN_STR"
echo "[entrypoint] Migrations applied successfully."

# ── Hand off to the app ──
# exec replaces this shell process with dotnet. The dotnet process becomes
# PID 1 in the container, so it receives signals from `docker stop` and
# shuts down gracefully.
echo "[entrypoint] Starting TakOne WebUI v1.0.0..."
exec dotnet TakOne.WebUI.dll
