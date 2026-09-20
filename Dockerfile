# =============================================================================
# Dockerfile — multi-stage build for the TakOne WebUI (.NET 10 + Blazor Server)
#
# IMAGE NAMING (professional convention):
#   - Image tag carries the version:        takone-web:1.0.0
#   - Container name describes its role:    takone-web   (functional, not versioned)
#   - Version metadata also lives in OCI image labels (org.opencontainers.image.version)
#   The "version-in-container-name" pattern (takone-web-1.0.0) is an anti-pattern —
#   containers are ephemeral; pinning a version to the container name forces a
#   rename on every upgrade and breaks any external reference to the container.
#
# BUILD STAGES:
#   Stage 1 "builder": full .NET SDK → restore + publish + build EF migrations bundle
#   Stage 2 "runtime": slim ASP.NET runtime image, ~250 MB
#
# MIGRATIONS BUNDLE:
#   The app doesn't call Database.Migrate() at startup. The efbundle is a
#   self-contained executable that applies all pending EF migrations against
#   the target DB. docker-entrypoint.sh runs it BEFORE starting the app, so
#   the schema exists by the time the app boots.
# =============================================================================

# ---- Stage 1: build + publish + migrations bundle ----
# Base image tags are PINNED to a specific patch version, NOT a floating
# tag. Floating tags (`10.0`) silently advance the patch level on every
# `docker build`, which breaks reproducibility and can introduce subtle
# runtime differences between builds a month apart. Bump these tags
# deliberately (after testing) when you want to pick up a new patch.
#
# VERSION NOTES (SDK vs runtime tag scheme):
#   - The .NET SDK image uses a THREE-DIGIT patch version (10.0.100, 10.0.101,
#     10.0.200, ... 10.0.401). The third digit is the SDK feature-band + patch.
#     Tags like `sdk:10.0.10` DO NOT EXIST on mcr.microsoft.com/dotnet/sdk.
#   - The .NET RUNTIME image (aspnet, runtime) uses the conventional two-digit
#     patch version that matches the runtime SemVer (10.0.0 ... 10.0.12).
#
#   The previous pin (`sdk:10.0.10` + `aspnet:10.0.10`) was inconsistent:
#   `aspnet:10.0.10` was valid, but `sdk:10.0.10` was NOT — Docker would fail
#   at the FROM line with "manifest unknown". This has been corrected below
#   to `sdk:10.0.401` (the SDK that ships with runtime 10.0.12, the latest
#   .NET 10 patch at the time of writing). The runtime stage is bumped to
#   `aspnet:10.0.12` to keep them in lockstep.
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS builder

WORKDIR /src

# Copy .csproj files FIRST (Docker layer caching: restore only re-runs when
# a package reference actually changes, not on every .cs file edit).
#
# NOTE ON SOLUTION FILE:
#   TakOne.slnx references ALL projects in the repo, including 9 test
#   projects under tests/*. The Dockerfile deliberately does NOT copy the
#   test csproj files (tests are not built into the runtime image — the
#   publish step below targets TakOne.WebUI only). Restoring the slnx
#   therefore fails with MSB3202 "project file ... was not found" for
#   every test csproj. We restore the WebUI csproj directly instead:
#   `dotnet restore` walks ProjectReference edges, so restoring WebUI
#   transitively restores Application, Domain, Infrastructure, SharedKernel,
#   and Analyzers — every project the publish + efbundle steps need. The
#   slnx is still COPY'd above (for reference / IDE compatibility inside
#   the container if someone shells in), but it is not the restore target.
COPY ["TakOne.slnx", "./"]
COPY ["TakOne.WebUI/TakOne.WebUI.csproj", "TakOne.WebUI/"]
COPY ["TakOne.Application/TakOne.Application.csproj", "TakOne.Application/"]
COPY ["TakOne.Domain/TakOne.Domain.csproj", "TakOne.Domain/"]
COPY ["TakOne.Infrastructure/TakOne.Infrastructure.csproj", "TakOne.Infrastructure/"]
COPY ["TakOne.SharedKernel/TakOne.SharedKernel.csproj", "TakOne.SharedKernel/"]
COPY ["TakOne.Analyzers/TakOne.Analyzers.csproj", "TakOne.Analyzers/"]
COPY ["TakOne.MigrationFixer/TakOne.MigrationFixer.csproj", "TakOne.MigrationFixer/"]

RUN dotnet restore "TakOne.WebUI/TakOne.WebUI.csproj"

# Now copy the rest of the source code.
COPY . .

# Publish the WebUI project (Release, no self-contained apphost — we run via dotnet).
RUN dotnet publish "TakOne.WebUI/TakOne.WebUI.csproj" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

# Publish the MigrationFixer — a tiny console app that marks existing
# migrations as applied in __EFMigrationsHistory before the efbundle runs.
# This handles the case where the database already has the schema (from a
# previous deploy) but __EFMigrationsHistory is missing the migration record.
# Without this, the efbundle tries to CREATE TABLE [AspNetRoles] ... and
# fails with SQL error 2714 "There is already an object named 'AspNetRoles'
# in the database." The MigrationFixer marks the migration as applied so
# the efbundle skips it and only runs genuinely new migrations (if any).
RUN dotnet publish "TakOne.MigrationFixer/TakOne.MigrationFixer.csproj" \
    -c Release \
    -o /app/migration-fixer \
    /p:UseAppHost=false

# Build the EF Core migrations bundle (single self-contained executable that
# applies all pending migrations to the target DB). Run from docker-entrypoint.sh.
RUN dotnet tool install --global dotnet-ef \
    && export PATH="$PATH:$HOME/.dotnet/tools" \
    && dotnet ef migrations bundle \
        --project "TakOne.Infrastructure/TakOne.Infrastructure.csproj" \
        --startup-project "TakOne.WebUI/TakOne.WebUI.csproj" \
        --configuration Release \
        --no-build \
        -o /app/efbundle


# ---- Stage 2: slim runtime image ----
# Pinned to a specific patch version (see Stage 1 comment for the SDK/runtime
# tag-versioning rationale). `aspnet:10.0.12` matches the .NET 10 runtime
# patch that ships with SDK 10.0.401 used in the builder stage.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS runtime

# OCI-standard image labels. `docker inspect takone-web` shows these; useful
# for inventory / auditing. Version is bumped on each release.
LABEL org.opencontainers.image.title="TakOne WebUI" \
      org.opencontainers.image.description="TakOne employee shop — Blazor Server web UI" \
      org.opencontainers.image.version="1.0.0" \
      org.opencontainers.image.vendor="TakOne" \
      org.opencontainers.image.licenses="Proprietary"

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_HTTP_PORTS=8080
ENV DOTNET_RUNNING_IN_CONTAINER=true

# .NET SingleFileApp bundle extraction directory.
# /app/efbundle is a SingleFileApp (built by `dotnet ef migrations bundle`)
# that extracts its embedded managed assemblies into a base directory before
# loading them. By default .NET uses $HOME/.net, but on the aspnet:10.0 image
# the `app` user has no home directory, and even if it did, the container
# runs read-only (see docker-compose.yml `read_only: true`), so the default
# location is unwritable. Without this env var, efbundle fails with
# "DOTNET_BUNDLE_EXTRACT_BASE_DIR is not set, and a read-write cache
# directory couldn't be created" and the container restart-loops on every
# migration run.
#
# /tmp is the only directory that's writable in a read-only container
# (via the tmpfs mount in docker-compose.yml). When the image is run
# standalone (without compose), /tmp is writable by default on Linux,
# so this still works.
#
# docker-compose.yml ALSO sets this env var explicitly (same value) so
# the contract is enforced at the orchestration layer too — but setting
# it here as the image default means the image is correct in isolation.
ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/dotnet-bundle

WORKDIR /app

# Copy the published app from the builder stage.
COPY --from=builder /app/publish ./

# Copy the migrations bundle.
COPY --from=builder /app/efbundle ./

# Copy the MigrationFixer tool (marks existing migrations as applied
# in __EFMigrationsHistory before the efbundle runs — prevents SQL
# error 2714 "There is already an object named 'AspNetRoles'" when
# the database already has the schema from a previous deploy).
COPY --from=builder /app/migration-fixer ./migration-fixer/

# Copy the entrypoint script and make it executable.
COPY docker-entrypoint.sh /app/docker-entrypoint.sh
RUN chmod +x /app/docker-entrypoint.sh

# Create the uploads directory OUTSIDE wwwroot (uploads are no longer
# served by the static-files middleware; a dedicated minimal-API endpoint
# serves them with auth + Content-Disposition: attachment). The directory
# is owned by the ASP.NET user so the runtime can write to it.
# See Brutal Code Review v3 finding #09.
# (uid 1654 = `app` on the aspnet:10.0 image.)
RUN mkdir -p /var/lib/takone/uploads \
    && chown -R app:app /var/lib/takone/uploads

# Install curl + netcat-openbsd. Both are tiny (~1MB total) and are NOT
# on the base aspnet:10.0 image by default. We need:
#   - curl          → the web container's healthcheck uses it
#                     (`curl -fsS http://localhost:8080/health` in
#                     docker-compose.yml — the ASP.NET Core /health
#                     endpoint wired in Program.cs)
#   - netcat-openbsd → docker-entrypoint.sh uses `nc -z` to TCP-probe
#                     SQL Server before running migrations. The previous
#                     entrypoint used `/dev/tcp/host/port`, but that's a
#                     bash-only builtin and /bin/sh on the aspnet:10.0
#                     image is dash, so the check always returned false
#                     and the wait loop never broke.
#
# Run as root (apt needs it), then drop back to the non-root `app` user.
ENV TZ=Asia/Tehran

USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl netcat-openbsd tzdata \
    && rm -rf /var/lib/apt/lists/*

# Switch to the non-root ASP.NET user. Running as root inside a container is
# bad practice — if the app is compromised, the attacker has root inside it.
USER app

EXPOSE 8080

# The entrypoint. Runs migrations first (waits for SQL Server to be ready,
# applies migrations), then starts the app.
ENTRYPOINT ["/app/docker-entrypoint.sh"]
