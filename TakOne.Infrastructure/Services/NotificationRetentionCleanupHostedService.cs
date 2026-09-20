using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Infrastructure.Persistence;

namespace TakOne.Infrastructure.Services;

/// <summary>
/// Periodically hard-deletes notification rows older than the retention
/// window (default 60 days) from BOTH the per-user <c>Notifications</c>
/// table AND the <c>BroadcastNotifications</c> audit table.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THIS EXISTS</b>: the <c>Notifications</c> table grew
/// unbounded before this service was added. Every sale lifecycle event
/// (Submitted / Approved / Invoiced / Cancelled) emits a domain event
/// that fans out into N per-user Notification rows (one per
/// recipient). At ~10 sales/day with 5 staff + 1 customer per sale,
/// that's ~60 rows/day = ~22k rows/year. After 5 years of operation,
/// the table holds 100k+ rows that NO user will ever scroll back to —
/// the <c>Notifications</c> page is newest-first paginated, so users
/// only ever see the first 1-3 pages.
/// </para>
/// <para>
/// <b>RETENTION WINDOW</b>: 60 days. Configurable via
/// <c>Notifications:RetentionDays</c> in <c>appsettings.json</c>.
/// 60 days is the sweet spot — covers "scroll back to find that
/// notification from last month" while keeping the table at most
/// ~60 days of rows (~3.6k rows in the steady state above).
/// </para>
/// <para>
/// <b>SCOPE</b>: the cleanup is system-wide — READ and UNREAD
/// notifications older than the cutoff are deleted, regardless of
/// the user. Per-user retention (e.g. "keep unread notifications
/// indefinitely") was rejected because:
/// <list type="bullet">
///   <item>The unread count badge would grow without bound for
///         disengaged users, creating a permanent "you have 412
///         unread" wall.</item>
///   <item>An attacker who never dismisses notifications could
///         quietly accumulate storage usage on the user's behalf.</item>
/// </list>
/// Age-based retention is simpler, fairer, and bounded.
/// </para>
/// <para>
/// <b>WHAT GETS DELETED</b>:
/// <list type="bullet">
///   <item><c>Notifications</c> rows where <c>CreatedAtUtc &lt; cutoff</c>
///         (per-user inbox rows — read and unread alike).</item>
///   <item><c>BroadcastNotifications</c> rows where <c>SentAtUtc &lt; cutoff</c>
///         (the audit record for admin-authored + auto-emitted app-
///         update broadcasts).</item>
/// </list>
/// Once the per-user fanout rows for a broadcast are gone, the audit
/// row has no remaining business value — keeping it indefinitely would
/// make the admin broadcast-list page longer and longer for no signal.
/// Aligned retention keeps the audit window equal to the inbox window.
/// </para>
/// <para>
/// <b>RUN CADENCE</b>: runs once per hour (configurable via
/// <c>Notifications:CleanupIntervalMinutes</c>, default 60). The
/// cleanup is a single DELETE per table — fast enough that hourly is
/// more than enough. The first run executes immediately on startup
/// (no delay) so a freshly-deployed container cleans up accumulated
/// old rows from before the cleanup existed; subsequent runs are
/// spaced by the interval.
/// </para>
/// <para>
/// <b>FAILURE IS NON-FATAL</b>: every step is wrapped in try/catch.
/// If the DB is unreachable, or the DELETE throws (e.g. deadlock with
/// a concurrent insert), the service logs a warning and continues.
/// The next scheduled run will retry. A failed cleanup is a minor
/// storage-efficiency issue, not a startup failure.
/// </para>
/// <para>
/// <b>WHY NOT A SQL AGENT JOB / CRON</b>: a .NET hosted service
/// keeps the retention logic in code (version-controlled, testable,
/// deployed with the app). A SQL Agent job or cron-driven
/// <c>sqlcmd</c> script would live outside the repo and silently
/// drift from the code's idea of "60 days" if the cutoff were ever
/// changed. The hosted service can also log to the same logger as
/// the rest of the app, surfacing the cleanup count in the same log
/// stream.
/// </para>
/// </remarks>
public sealed class NotificationRetentionCleanupHostedService : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(1);
    private static readonly int DefaultRetentionDays = 60;

    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NotificationRetentionCleanupHostedService> _logger;

    public NotificationRetentionCleanupHostedService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<NotificationRetentionCleanupHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Read retention + interval from configuration. Allow override
        // for integration tests.
        var retentionDays = _configuration.GetValue<int?>("Notifications:RetentionDays")
            ?? DefaultRetentionDays;
        var intervalMinutes = _configuration.GetValue<double?>("Notifications:CleanupIntervalMinutes");

        var interval = intervalMinutes.HasValue
            ? TimeSpan.FromMinutes(intervalMinutes.Value)
            : DefaultInterval;

        // Safety floor: never run more than once per minute. A
        // misconfigured interval of 0 would otherwise spin the cleanup
        // in a tight loop and starve the rest of the app.
        if (interval < TimeSpan.FromMinutes(1))
        {
            interval = TimeSpan.FromMinutes(1);
        }

        // Safety floor on retention: never retain less than 1 day.
        // A misconfigured RetentionDays=0 would delete EVERY notification
        // on every cleanup run — making the notification system useless.
        if (retentionDays < 1)
        {
            retentionDays = DefaultRetentionDays;
        }

        _logger.LogInformation(
            "NotificationRetentionCleanupHostedService: started. " +
            "Retention = {RetentionDays} days, cleanup interval = {IntervalMinutes:F1} minutes.",
            retentionDays, interval.TotalMinutes);

        // First run executes IMMEDIATELY — no startup delay. A freshly
        // deployed container with old accumulated rows from before the
        // retention service existed needs the cleanup right away.
        // Subsequent runs are spaced by the interval.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCleanupAsync(retentionDays, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Expected on graceful shutdown — break the loop.
                break;
            }
            catch (Exception ex)
            {
                // Don't let a single cleanup failure crash the hosted
                // service — log and continue. The next tick will retry.
                _logger.LogError(ex,
                    "NotificationRetentionCleanupHostedService: cleanup threw an " +
                    "unexpected exception. The next scheduled cleanup will retry. " +
                    "Old notification rows may accumulate until a successful cleanup run.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunCleanupAsync(int retentionDays, CancellationToken cancellationToken)
    {
        // Compute the cutoff once per run. Both tables use the same cutoff
        // so the audit window stays aligned with the inbox window.
        var cutoffUtc = DateTime.UtcNow - TimeSpan.FromDays(retentionDays);

        // BackgroundService is a Singleton; we can't inject scoped services
        // (the repositories + DbContext are Scoped). Create a scope per
        // execution and resolve from there.
        using var scope = _serviceProvider.CreateScope();
        var notificationRepo = scope.ServiceProvider.GetRequiredService<INotificationRepository>();
        var broadcastRepo = scope.ServiceProvider.GetRequiredService<IBroadcastNotificationRepository>();

        // Per-user notifications — READ + UNREAD, all users, age-based.
        var notificationsDeleted = await notificationRepo.DeleteOlderThanAsync(cutoffUtc, cancellationToken);

        // Broadcast audit rows — aligned retention window.
        var broadcastsDeleted = await broadcastRepo.DeleteOlderThanAsync(cutoffUtc, cancellationToken);

        // Only log when we actually deleted something — otherwise the
        // logs are noisy with hourly "deleted 0 rows" entries that
        // provide no signal. The first run on a fresh deploy will
        // likely delete a chunk; steady-state runs will mostly delete
        // ~60-day-old rows from yesterday's hourly batches.
        if (notificationsDeleted > 0 || broadcastsDeleted > 0)
        {
            _logger.LogInformation(
                "NotificationRetentionCleanup: deleted {NotificationsCount} notification(s) " +
                "and {BroadcastsCount} broadcast(s) older than {CutoffUtc:O} ({RetentionDays} days).",
                notificationsDeleted, broadcastsDeleted, cutoffUtc, retentionDays);
        }
    }
}
