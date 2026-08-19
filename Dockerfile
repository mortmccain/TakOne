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
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS builder

WORKDIR /src

# Copy .csproj files FIRST (Docker layer caching: restore only re-runs when
# a package reference actually changes, not on every .cs file edit).
COPY ["TakOne.slnx", "./"]
COPY ["TakOne.WebUI/TakOne.WebUI.csproj", "TakOne.WebUI/"]
COPY ["TakOne.Application/TakOne.Application.csproj", "TakOne.Application/"]
COPY ["TakOne.Domain/TakOne.Domain.csproj", "TakOne.Domain/"]
COPY ["TakOne.Infrastructure/TakOne.Infrastructure.csproj", "TakOne.Infrastructure/"]
COPY ["TakOne.SharedKernel/TakOne.SharedKernel.csproj", "TakOne.SharedKernel/"]
COPY ["TakOne.Analyzers/TakOne.Analyzers.csproj", "TakOne.Analyzers/"]
COPY ["TakOne.Analyzer/TakOne.Analyzer.csproj", "TakOne.Analyzer/"]

RUN dotnet restore "TakOne.slnx"

# Now copy the rest of the source code.
COPY . .

# Publish the WebUI project (Release, no self-contained apphost — we run via dotnet).
RUN dotnet publish "TakOne.WebUI/TakOne.WebUI.csproj" \
    -c Release \
    -o /app/publish \
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
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

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

WORKDIR /app

# Copy the published app from the builder stage.
COPY --from=builder /app/publish ./

# Copy the migrations bundle.
COPY --from=builder /app/efbundle ./

# Copy the entrypoint script and make it executable.
COPY docker-entrypoint.sh /app/docker-entrypoint.sh
RUN chmod +x /app/docker-entrypoint.sh

# Create the uploads directory and make it writable by the ASP.NET user.
# (uid 1654 = `app` on the aspnet:10.0 image.)
RUN mkdir -p /app/wwwroot/uploads \
    && chown -R app:app /app/wwwroot/uploads

# Install curl + netcat-openbsd. Both are tiny (~1MB total) and are NOT
# on the base aspnet:10.0 image by default. We need:
#   - curl          → the web container's healthcheck uses it
#                     (`curl -fsS http://localhost:8080/` in docker-compose.yml)
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
