# =============================================================================
# Dockerfile — multi-stage build for the TakOne WebUI (.NET 10 + Blazor Server)
#
# WHAT THIS DOES (high level):
#   Stage 1 "builder": uses the full .NET SDK to restore NuGet packages,
#                       build the solution, publish a release build, AND
#                       generate a self-contained EF Core migrations bundle.
#   Stage 2 "runtime": copies ONLY the published app + the migrations bundle
#                       into a slim ASP.NET runtime image. Result: ~250MB
#                       instead of ~1.5GB.
#
# WHY A MIGRATIONS BUNDLE:
#   Your app has an EF Core migration (InitialCreate) but nothing in
#   Program.cs calls `Database.Migrate()` at startup. The efbundle is a
#   single self-contained executable that applies all pending migrations
#   against the database. We run it from docker-entrypoint.sh BEFORE
#   starting the app — so by the time the app boots, the schema exists.
#
# IMAGE TAGS:
#   We use mcr.microsoft.com/dotnet/sdk:10.0 and aspnet:10.0 (stable GA
#   tags for .NET 10). If Microsoft hasn't shipped 10.0 stable yet on
#   your build host, swap to the nightly tag: mcr.microsoft.com/dotnet/nightly/sdk:10.0
# =============================================================================

# ---- Stage 1: build + publish + migrations bundle ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS builder

# The solution file lives at the repo root. Set WORKDIR to /src so all
# subsequent COPY + RUN commands are relative to /src.
WORKDIR /src

# Copy ONLY the project files + NuGet.config (if any) FIRST, then run
# `dotnet restore`. Why? Docker layer caching. If you copy everything
# in one shot, ANY change to a .cs file invalidates the restore layer
# and Docker re-downloads all NuGet packages on every build. Copying
# .csproj files first means restore only re-runs when a package
# reference actually changes.
COPY ["TakOne.slnx", "./"]
COPY ["TakOne.WebUI/TakOne.WebUI.csproj", "TakOne.WebUI/"]
COPY ["TakOne.Application/TakOne.Application.csproj", "TakOne.Application/"]
COPY ["TakOne.Domain/TakOne.Domain.csproj", "TakOne.Domain/"]
COPY ["TakOne.Infrastructure/TakOne.Infrastructure.csproj", "TakOne.Infrastructure/"]
COPY ["TakOne.SharedKernel/TakOne.SharedKernel.csproj", "TakOne.SharedKernel/"]
COPY ["TakOne.Analyzers/TakOne.Analyzers.csproj", "TakOne.Analyzers/"]
COPY ["TakOne.Analyzer/TakOne.Analyzer.csproj", "TakOne.Analyzer/"]

# Restore NuGet packages for the whole solution.
RUN dotnet restore "TakOne.slnx"

# Now copy the rest of the source code.
COPY . .

# Publish the WebUI project as a Release build to /app/publish.
#   --no-restore : we already restored above, don't waste time doing it again
#   -c Release   : Release configuration (optimized, no debug overhead)
#   -o /app/publish : output directory
# We publish TakOne.WebUI specifically (not the whole solution) because
# the WebUI is the only runnable project — it pulls in the others as
# project references automatically.
RUN dotnet publish "TakOne.WebUI/TakOne.WebUI.csproj" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

# Build the EF Core migrations bundle.
# This produces a single executable `efbundle` at /app/efbundle that,
# when run, applies ALL pending EF migrations to a target database.
# It needs to know:
#   --project        : the project that contains the migrations (Infrastructure)
#   --startup-project: the project that has the DbContext registered (WebUI)
#   -o               : output path
#   --no-build       : don't re-build (we just published above)
#
# The resulting `efbundle` is a .NET executable targeting net10.0. The
# runtime image (aspnet:10.0) has the .NET 10 runtime installed, so
# running `./efbundle` in the runtime container just works.
#
# Note: dotnet-ef tools are backwards-compatible with older EF Core runtime
# versions, so we install the latest. Pinning to a specific version like
# 10.0.10 risks failing if that exact patch hasn't shipped yet.
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

# Tell ASP.NET Core we're in Production. This:
#   - Turns on optimized view compilation
#   - Reduces log verbosity
#   - Disables developer exception pages
#   - Makes the appsettings.Production.json file get loaded
ENV ASPNETCORE_ENVIRONMENT=Production

# ASP.NET Core 8+ listens on port 8080 by default inside a container.
# This env var makes it explicit (and overrides any launchSettings.json).
ENV ASPNETCORE_HTTP_PORTS=8080

# Tell the dotnet runtime to listen on all interfaces inside the
# container. (Default is fine for most cases, but explicit is safer.)
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Where the app will live in the runtime image.
WORKDIR /app

# Copy the published app from the builder stage.
# We use --from=builder to pull from the previous stage, not from the
# build context on disk.
COPY --from=builder /app/publish ./

# Copy the migrations bundle.
COPY --from=builder /app/efbundle ./

# Copy the entrypoint script and make it executable.
# Note: chmod has to happen via a RUN, not via COPY, because COPY
# doesn't preserve Unix permission bits reliably.
COPY docker-entrypoint.sh /app/docker-entrypoint.sh
RUN chmod +x /app/docker-entrypoint.sh

# Create the uploads directory and make it writable by the ASP.NET
# user (uid 1654 on the aspnet:10.0 image). Product image uploads
# land here; we mount a volume on this path in docker-compose.yml so
# uploads survive container rebuilds.
RUN mkdir -p /app/wwwroot/uploads \
    && chown -R app:app /app/wwwroot/uploads

# Switch to the non-root ASP.NET user. Running as root inside a
# container is bad practice — if the app is compromised, the attacker
# has root inside the container.
USER app

# Expose port 8080 to the host. This is documentation only — actual
# port mapping happens in docker-compose.yml or `docker run -p`.
EXPOSE 8080

# The entrypoint. This runs the migrations bundle first (waits for
# SQL Server to be ready, applies migrations), then starts the app.
ENTRYPOINT ["/app/docker-entrypoint.sh"]
