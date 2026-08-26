using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Notifications.Commands.EmitAppUpdateBroadcast;

namespace TakOne.WebUI.Services;

/// <summary>
/// Background service that runs once at app startup and broadcasts an
/// <see cref="TakOne.Domain.Notifications.Enums.NotificationKind.AppUpdate"/>
/// notification to every active user when the running assembly version
/// differs from <c>SystemSettings.LastKnownAppVersion</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE PROBLEM THIS SOLVES</b>: when you deploy a new version of the
/// app via Docker Compose (<c>git pull && docker compose up -d --build</c>),
/// the running code changes — but users currently browsing the OLD
/// container's UI don't know. Their next page navigation could either:
/// <list type="bullet">
///   <item>Hit a stale Blazor circuit on the OLD container (if the
///         container swap hasn't fully propagated) → confusing errors.</item>
///   <item>Get routed to the NEW container with a stale SignalR
///         connection → circuit-reconnect dance.</item>
/// </list>
/// A polite "TakOne was updated to v1.2.0 — click Reload" notification
/// lets users gracefully refresh to pick up the new code.
/// </para>
/// <para>
/// <b>HOW IT WORKS</b>:
/// <list type="number">
///   <item>On startup, read the running assembly's
///         <c>AssemblyInformationalVersionAttribute</c> (e.g. "1.2.0").</item>
///   <item>Load <c>SystemSettings.LastKnownAppVersion</c> from the DB.</item>
///   <item>If they differ AND the persisted value is NOT null (skip on
///         first boot — there's nothing to "update from" on a fresh install),
///         dispatch <see cref="EmitAppUpdateBroadcastCommand"/> via
///         <c>IMessageBus.PublishAsync</c>. The command fans out to every
///         active user with Kind=AppUpdate (the broadcast pipeline creates
///         the audit row + per-user Notification rows + raises
///         NotificationCreatedDomainEvent per row → SignalR ping per user,
///         all in one Wolverine transaction).</item>
///   <item>Persist the new version to
///         <c>SystemSettings.LastKnownAppVersion</c> so subsequent restarts
///         with the same version don't re-broadcast.</item>
/// </list>
/// </para>
/// <para>
/// <b>FAILURE IS NON-FATAL</b>: every step is wrapped in try/catch. If
/// the DB is unreachable, or Wolverine dispatch fails, or the assembly
/// attribute is missing, the service logs a warning and returns — the
/// app continues to boot. Users who miss the broadcast will see the new
/// UI on next page load anyway (the running code IS the new code).
/// </para>
/// <para>
/// <b>WHY A HOSTED SERVICE (not a one-shot startup task)</b>: hosted
/// services run AFTER the DI container is built and AFTER Wolverine's
/// bus is initialized. <c>IMessageBus.PublishAsync</c> works from here.
/// A one-shot <c>IHostApplicationLifetime.ApplicationStarted</c> callback
/// would also work, but BackgroundService gives us the cleaner async
/// lifecycle + cancellation token support.
/// </para>
/// <para>
/// <b>WHY A SHORT DELAY BEFORE RUNNING</b>: Wolverine's worker threads
/// take a moment to spin up. A 2-second delay (only on the version-check
/// path) lets the bus settle before we publish. The delay is not strictly
/// necessary — Wolverine queues messages durably and processes them when
/// ready — but it reduces "first publish" warning noise in the logs.
/// </para>
/// <para>
/// <b>WHY WE INVALIDATE THE SETTINGS CACHE</b>: the
/// <c>ISystemSettingsService</c> caches the SystemSettings row in-process.
/// After we write the new <c>LastKnownAppVersion</c>, we invalidate the
/// cache so the next read picks up the new value. Otherwise, a stale
/// cached value could trigger a spurious second broadcast on the next
/// request that hits the cache before it expires.
/// </para>
/// </remarks>
public sealed class AppUpdateBroadcasterHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AppUpdateBroadcasterHostedService> _logger;
    private readonly IHostEnvironment _environment;

    public AppUpdateBroadcasterHostedService(
        IServiceProvider serviceProvider,
        ILogger<AppUpdateBroadcasterHostedService> logger,
        IHostEnvironment environment)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _environment = environment;
    }

    /// <summary>
    /// The delay before the version-check runs. Lets Wolverine's worker
    /// threads spin up so the first <c>PublishAsync</c> doesn't log a
    /// "no handlers yet" warning. 2 seconds is generous — Wolverine is
    /// usually ready in &lt;100ms.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Wait briefly for the host to settle (Wolverine + the DB
            // connection pool warm up). We don't wait on
            // IHostApplicationLifetime.ApplicationStarted because
            // BackgroundService.ExecuteAsync already runs after the host
            // is built; this delay is purely for Wolverine's worker
            // readiness.
            await Task.Delay(StartupDelay, stoppingToken);

            await RunVersionCheckAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down before our delay completed — exit silently.
        }
        catch (Exception ex)
        {
            // CRITICAL: never let the hosted service crash the app. A
            // failed app-update broadcast is a minor UX issue, not a
            // startup failure. Log + swallow.
            _logger.LogWarning(ex,
                "AppUpdateBroadcasterHostedService: version check failed (app continues to boot).");
        }
    }

    private async Task RunVersionCheckAsync(CancellationToken cancellationToken)
    {
        // ── 1. Read the running assembly's informational version. ──
        // AssemblyInformationalVersionAttribute is set by the .NET SDK
        // from <Version> or <InformationalVersion> in the .csproj. If
        // not explicitly set, defaults to the assembly version (e.g.
        // "1.0.0"). We use this assembly (the hosted service's) because
        // it lives in the WebUI project which is the entry-point
        // assembly.
        var assemblyVersion = typeof(AppUpdateBroadcasterHostedService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?
            .Trim();

        if (string.IsNullOrEmpty(assemblyVersion))
        {
            _logger.LogDebug(
                "AppUpdateBroadcasterHostedService: no AssemblyInformationalVersion attribute found — skipping version check.");
            return;
        }

        // ── 2. Create a DI scope + resolve deps. ──
        // BackgroundService is a Singleton; we can't inject scoped
        // services (ISystemSettingsRepository, IMessageBus) directly.
        // Create a scope per execution and resolve from there.
        using var scope = _serviceProvider.CreateScope();
        var settingsRepo = scope.ServiceProvider.GetRequiredService<ISystemSettingsRepository>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISystemSettingsService>();
        var messageBus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        // ── 3. Load the persisted last-known version. ──
        var settings = await settingsRepo.GetOrCreateAsync(cancellationToken);
        var persistedVersion = settings.LastKnownAppVersion;

        // ── 4. Decide: broadcast or skip? ──
        // - persistedVersion == null → fresh install OR first boot of
        //   this feature → write the version, but DON'T broadcast (no
        //   point announcing an "update" when there's nothing to update
        //   from).
        // - persistedVersion == assemblyVersion → same version as last
        //   boot → no broadcast, no write (idempotent — the
        //   UpdateLastKnownAppVersion method is a no-op on equal values).
        // - persistedVersion != assemblyVersion → the version changed
        //   since last boot → BROADCAST.
        var shouldBroadcast = persistedVersion is not null
            && !string.Equals(persistedVersion, assemblyVersion, StringComparison.Ordinal);

        if (shouldBroadcast)
        {
            _logger.LogInformation(
                "AppUpdateBroadcasterHostedService: app version changed '{Old}' → '{New}'. Broadcasting AppUpdate notification to all users.",
                persistedVersion, assemblyVersion);

            await BroadcastAppUpdateAsync(messageBus, assemblyVersion, persistedVersion!, cancellationToken);
        }
        else if (persistedVersion is null)
        {
            _logger.LogInformation(
                "AppUpdateBroadcasterHostedService: first boot (no persisted version) — recording '{Version}', no broadcast.",
                assemblyVersion);
        }
        else
        {
            _logger.LogDebug(
                "AppUpdateBroadcasterHostedService: same version '{Version}' as last boot — no broadcast, no write.",
                assemblyVersion);
            // No write needed — UpdateLastKnownAppVersion is a no-op on
            // equal values, but we can also just early-return here.
            return;
        }

        // ── 5. Persist the new version + invalidate cache. ──
        // UpdateLastKnownAppVersion is idempotent — if the value didn't
        // change, it's a no-op (no spurious DB UPDATE).
        settings.UpdateLastKnownAppVersion(assemblyVersion);
        await settingsRepo.UpdateAsync(settings, cancellationToken);
        await settingsService.InvalidateCacheAsync(cancellationToken);
    }

    /// <summary>
    /// Dispatches the <see cref="EmitAppUpdateBroadcastCommand"/> via
    /// Wolverine's IMessageBus. The command's handler fans out the
    /// per-user Notification rows in a Wolverine transaction (audit row +
    /// N fanout rows + N SignalR pings, all atomic).
    /// </summary>
    private static async Task BroadcastAppUpdateAsync(
        IMessageBus messageBus,
        string newVersion,
        string oldVersion,
        CancellationToken cancellationToken)
    {
        // Compose the title + message. Kept short so they fit in the
        // notification bell badge preview + the desktop toast.
        //
        // NOTE: these strings are CULTURE-NEUTRAL placeholders that the
        // UI localizes at render time via the AppUpdate-specific resx
        // keys. Wait — actually the broadcast message is admin-authored
        // free-form text that gets persisted VERBATIM into the per-user
        // Notification rows. So these strings ARE the user-facing text.
        // For now they're English; a future enhancement would localize
        // by emitting N broadcasts (one per culture) or by storing
        // resource keys + format args instead of literal text. The
        // existing Notification aggregate's structured-only design
        // (Kind + structured fields) doesn't extend cleanly to free-form
        // broadcast text — this is a known trade-off. The current
        // implementation favors simplicity: every user sees the same
        // English message. Most enterprise apps do this for system
        // messages.
        var title = $"TakOne updated to v{newVersion}";
        var message = $"The application has been updated from v{oldVersion} to v{newVersion}. " +
                      "Please reload the page to load the new version.";

        var command = new EmitAppUpdateBroadcastCommand(title, message);
        // Wolverine's IMessageBus.PublishAsync signature doesn't take a
        // CancellationToken — it queues durably to the message store and
        // returns. Wolverine's worker threads process the message with
        // their own cancellation semantics. We don't need to plumb our
        // host's cancellation token here.
        await messageBus.PublishAsync(command);
    }
}
