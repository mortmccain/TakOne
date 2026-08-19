# TakOne — Docker deployment (DK1)

Production-ready Docker setup for the TakOne employee shop (.NET 10 Blazor
Server + SQL Server 2022). Drop these files into the project root and run.

## Files in this package

| File | Purpose |
|---|---|
| `Dockerfile` | Multi-stage build for the .NET 10 WebUI. Stage 1 builds + generates EF migrations bundle; stage 2 is the slim ASP.NET runtime image (~250 MB). |
| `docker-compose.yml` | Orchestration: 2 services (`takone-db` SQL Server, `takone-web` Blazor app) + 2 named volumes (`takone_sqldata`, `takone_uploads`). |
| `docker-entrypoint.sh` | Container start script. Waits for SQL Server, runs EF migrations bundle, then starts the .NET app. |
| `appsettings.Production.json` | Production config (overrides base `appsettings.json` when `ASPNETCORE_ENVIRONMENT=Production`). Holds the same connection string + admin password as `docker-compose.yml`. |
| `.dockerignore` | Excludes `bin/`, `obj/`, `.git/`, IDE state, etc. from the Docker build context. |
| `.env.example` | Optional template for those who prefer secrets-in-.env over baked-in. |
| `README.md` | This file. |

## Naming convention (why container names are NOT versioned)

Industry standard practice:

- **Image tag carries the version:** `takone-web:1.0.0`
- **Container name describes its role:** `takone-web`, `takone-db`

Putting the version in the container name (e.g. `takone-web-1.0.0`) is an
anti-pattern — every upgrade forces a rename, breaking any external reference
(DNS, monitoring, log forwarders, scripts). The version is instead stored as
an OCI label on the image:

```bash
docker inspect takone-web --format '{{ index .Config.Labels "org.opencontainers.image.version" }}'
# → 1.0.0
```

Bump the version by editing `image: takone-web:1.0.0` in `docker-compose.yml`
and the `org.opencontainers.image.version` label in `Dockerfile` together.

## Prerequisites

- Docker Engine 24+ (with the `docker compose` v2 plugin)
- 4 GB free RAM (SQL Server takes ~3 GB, the app ~1 GB)
- Linux x86_64 host (the SQL Server Linux image is x86_64 only)

## Quick start

1. **Drop the files at the project root** (alongside `TakOne.slnx`):

   ```bash
   cp Dockerfile docker-compose.yml docker-entrypoint.sh appsettings.Production.json .dockerignore /path/to/TakOne/
   cp appsettings.Production.json /path/to/TakOne/TakOne.WebUI/
   ```

2. **Build + start the stack:**

   ```bash
   cd /path/to/TakOne
   docker compose up -d --build
   ```

   First build takes 4–8 minutes (downloads the .NET 10 SDK image, restores
   NuGet packages, publishes, builds the EF migrations bundle). Subsequent
   builds are faster thanks to Docker layer caching.

3. **Watch the app come up:**

   ```bash
   docker compose logs -f takone-web
   ```

   You should see:
   ```
   [entrypoint] TakOne WebUI v1.0.0 — waiting for SQL Server at takone-db:1433...
   [entrypoint] SQL Server is accepting TCP connections.
   [entrypoint] Applying EF Core migrations...
   [entrypoint] Migrations applied successfully.
   [entrypoint] Starting TakOne WebUI v1.0.0...
   info: Microsoft.Hosting.Lifetime[14] Now listening on: http://[::]:8080
   ```

   Press `Ctrl+C` to detach from the log stream (containers keep running).

4. **Open the app:**

   ```
   http://YOUR-SERVER-IP:8080
   ```

5. **Log in with the default admin:**

   - **Worker ID:** `ADMIN-0001`
   - **Password:** `M4k4ron!T0p#2025`

   You'll be forced to change the password on first login
   (`ForcePasswordChangeOnFirstLogin=true` in the seeder).

## Credentials baked into this deployment

Per the operator's explicit request, real working passwords are baked into
`docker-compose.yml` and `appsettings.Production.json`:

| Secret | Value | Used by |
|---|---|---|
| SQL Server SA password | `Tk1!S4ltyM4c4ron#2025` | `takone-db` (MSSQL_SA_PASSWORD) + connection strings |
| Default admin initial password | `M4k4ron!T0p#2025` | `TakOne__Database__DefaultAdmin__Password` env + `appsettings.Production.json` |

The admin password is single-use — the seeder forces a password change on
first login. The SA password persists across container restarts (it's the
SQL Server system administrator's password). To rotate the SA password later:

1. Edit `MSSQL_SA_PASSWORD` in `docker-compose.yml` (and the matching
   password in both connection strings + `appsettings.Production.json`).
2. `docker compose down && docker compose up -d --build`

The `takone_sqldata` volume survives `down`/`up` — the existing database is
preserved across rebuilds. Only `docker compose down -v` (with `-v`) nukes
the volume; never run that command unless you intend to wipe the database.

## Connecting to SQL Server from your laptop

`takone-db` exposes port 1433 on the host, so you can connect from SSMS /
Azure Data Studio / `sqlcmd` for debugging:

```bash
sqlcmd -S your-server-ip,1433 -U sa -P 'Tk1!S4ltyM4c4ron#2025' -C -Q "SELECT name FROM sys.databases"
```

To disable external DB access, comment out the `ports: - "1433:1433"` block
under `takone-db` in `docker-compose.yml`. The web container will still be
able to reach the DB over Docker's internal network (`takone-db:1433`).

## Operations

| Task | Command |
|---|---|
| View live logs | `docker compose logs -f takone-web` |
| View DB logs | `docker compose logs -f takone-db` |
| Restart the app only | `docker compose restart takone-web` |
| Stop the stack | `docker compose down` |
| Start the stack again | `docker compose up -d` |
| Rebuild after code changes | `docker compose up -d --build` |
| Check container health | `docker compose ps` |
| Open a shell inside the app | `docker compose exec takone-web bash` |
| Open SQL shell | `docker compose exec takone-db /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'Tk1!S4ltyM4c4ron#2025' -C` |

## Health checks

Both services have health checks defined:

- `takone-db`: pings SQL Server with `SELECT 1` every 10s.
- `takone-web`: hits `http://localhost:8080/` every 15s (after a 45s grace period for migrations + boot).

`docker compose ps` shows the health status. `takone-web` won't start until
`takone-db` reports `healthy` (via `depends_on: condition: service_healthy`).

## Troubleshooting

**`MSSQL_SA_PASSWORD` is too weak.** SQL Server 2022 refuses to boot and
logs `ERROR: Unable to set system administrator password`. Use a password
with 8+ chars and 3 of 4 categories (upper, lower, digit, symbol). The
baked-in password above already meets these rules.

**EF migrations fail with `Login failed for user 'sa'`.** SQL Server wasn't
ready yet when the efbundle ran. The entrypoint's wait loop should prevent
this, but if the DB is slow to boot, bump `MAX_ATTEMPTS` in
`docker-entrypoint.sh` from 30 to 60 (extends the max wait to 120s).

**App boots but `Database.Migrate` errors in logs.** This shouldn't happen —
the entrypoint runs the migrations bundle BEFORE the app starts, so by the
time the app boots the schema exists. If you see it, check that the
connection string in `appsettings.Production.json` matches the one in
`docker-compose.yml`'s `TAKONE_CONNECTION_STRING` env var exactly.

**`docker compose up` says `service_healthy` is unsupported.** Your Docker
Engine is too old. Upgrade to Docker Engine 24+ (or Docker Desktop 4.20+).

## Backups

The database lives in the `takone_sqldata` named volume. To back it up:

```bash
# Stop the app (so no writes happen during backup)
docker compose stop takone-web

# Dump the database to a .bak file inside the container
docker compose exec takone-db /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P 'Tk1!S4ltyM4c4ron#2025' -C \
  -Q "BACKUP DATABASE TakOne TO DISK='/var/opt/mssql/data/TakOne.bak'"

# Copy the .bak file out to the host
docker compose cp takone-db:/var/opt/mssql/data/TakOne.bak ./TakOne.bak

# Restart the app
docker compose start takone-web
```

## What's NOT in this package

- **HTTPS / TLS termination.** This setup is HTTP-only on port 8080. For a
  public-facing deployment, put Caddy or Nginx in front as a reverse proxy
  that handles TLS automatically. One step at a time.
- **Email / SMTP.** The app has no email-sending feature wired up yet.
- **Backups automation.** The commands above are manual. For production,
  set up a cron job or a `docker compose run --rm` scheduled task.
- **Monitoring.** No Prometheus / Grafana / OpenTelemetry. The app does
  log to stdout/stderr in JSON-friendly format; `docker compose logs` is
  the primary observability surface.
