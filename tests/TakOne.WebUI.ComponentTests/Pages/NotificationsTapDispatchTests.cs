using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TakOne.Application.Notifications.Commands.MarkNotificationAsRead;
using TakOne.Application.Notifications.DTOs;
using TakOne.Application.Notifications.Queries.GetNotificationsForUser;
using TakOne.Application.Notifications.Queries.GetUnreadNotificationCount;
using TakOne.Application.Resources;
using TakOne.Domain.Notifications.Enums;
using TakOne.SharedKernel.Common;
using TakOne.WebUI.Components.MobilePages.MobileNotifications;
using TakOne.WebUI.Components.Pages.Notifications;
using TakOne.WebUI.Services;
using Wolverine;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Pages;

/// <summary>
/// Regression tests for the notification TAP dispatch path
/// (<c>HandleTapAsync</c>) on BOTH the desktop (<see cref="Notifications"/>)
/// and mobile (<see cref="MobileNotifications"/>) pages.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE PRODUCTION INCIDENT THESE PIN</b>: tapping the auto-emitted
/// "app updated" notification used to dispatch
/// <see cref="MarkNotificationAsReadCommand"/> from a fire-and-forget
/// <c>_ = Task.Run(...)</c> and then immediately
/// <c>NavigateTo(uri, forceLoad: true)</c>. The force-load tears the
/// Blazor circuit down while the background dispatch races it; Wolverine
/// executes inline handlers against a child DI scope resolved from the
/// ROOT provider, so the handler's <c>ICurrentUserService</c> can only
/// see the user through the AMBIENT ExecutionContext (the /_blazor hub
/// request's HttpContext). When the teardown wins the race the ambient
/// state is gone, the child scope's fresh AuthenticationStateProvider
/// throws, <c>BlazorCurrentUserService</c> degrades to anonymous, and the
/// fail-closed AuthorizationMiddleware rejects the command with
/// "Authentication required" — for a logged-in user:
///
/// <code>
///   fail: TakOne.Application.Notifications.Commands.MarkNotificationAsRead...
///         MessageAuthorizationException: Authentication required.
///         at AuthorizationMiddleware.Before(Envelope envelope)
/// </code>
/// </para>
/// <para>
/// <b>THE FIX (what these prove)</b>: the tap handler now AWAITS the
/// mark-read dispatch inside the circuit's event dispatch — the same
/// ExecutionContext every other command on the page already uses — and
/// only THEN navigates.
/// </para>
/// <para>
/// <b>HOW THE PIN IS MADE DETERMINISTIC</b>: a naive ordering assertion
/// (command recorded before navigation) is NOT a reliable discriminator
/// in bUnit — a fire-and-forget <c>Task.Run</c> dispatch is fast enough
/// to win that race in a test even though it loses it in production.
/// So the fake bus returns the command's task from a
/// <see cref="TaskCompletionSource{TResult}"/> GATE that the test holds
/// open: while the mark-read "round-trip" is in flight, the handler must
/// be BLOCKED (no navigation may be requested). Under the old
/// fire-and-forget code the navigation was requested immediately — while
/// the gate was still open — so these tests fail deterministically
/// against a regression to <c>Task.Run</c>, and pass only when the
/// dispatch is genuinely awaited.
/// </para>
/// <para>
/// <b>WIRING</b>: same harness discipline as
/// <see cref="SalesPagePendingChipTests"/> — a recording fake bus
/// (sequence-based assertions, not exact counts), the identity
/// localizer, a recording <see cref="NavigationManager"/> (bUnit's
/// default throws on <c>forceLoad: true</c> — the AppUpdate tap relies
/// on it, so we must supply one that records instead), a real
/// <see cref="NotificationRefreshService"/> over bUnit's loose JS runtime
/// (the page only subscribes to its event; no JS calls happen at page
/// level), and the singleton ToastService override (Radzen registers
/// NotificationService as scoped, which bUnit's root provider cannot
/// resolve).
/// </para>
/// </remarks>
public class NotificationsTapDispatchTests
{
    /// <summary>
    /// The recording harness shared by every test in this class.
    /// </summary>
    /// <remarks>
    /// <para><b><see cref="Gate"/></b>: the fake bus's return task for
    /// <see cref="MarkNotificationAsReadCommand"/>. Held open by the test
    /// to prove the tap handler genuinely awaits the dispatch (see the
    /// class remarks for why this is the deterministic regression
    /// pin).</para>
    /// <para><b><see cref="Timeline"/></b>: the shared, ordered event log.
    /// Every bus dispatch records <c>bus:&lt;message&gt;</c> and every
    /// navigation records <c>nav:&lt;uri&gt;|forceLoad=&lt;bool&gt;</c>.</para>
    /// <para><b><see cref="MarkedRead"/></b>: every dispatched
    /// <see cref="MarkNotificationAsReadCommand"/> instance (for the
    /// notification-id assertion).</para>
    /// </remarks>
    private sealed record Harness(
        BunitContext Context,
        TaskCompletionSource<Result> Gate,
        List<string> Timeline,
        List<MarkNotificationAsReadCommand> MarkedRead) : IDisposable
    {
        public void Dispose() => Context.Dispose();

        /// <summary>
        /// Waits (polling from the TEST thread — never the renderer's)
        /// until the timeline satisfies <paramref name="condition"/>, or
        /// fails the test on timeout.
        /// </summary>
        public async Task WaitForTimelineAsync(
            Func<IReadOnlyList<string>, bool> condition, string because,
            TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
            while (DateTime.UtcNow < deadline)
            {
                lock (Timeline)
                {
                    if (condition(Timeline))
                    {
                        return;
                    }
                }
                await Task.Delay(25);
            }

            lock (Timeline)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Timed out waiting for the timeline: {because}. " +
                    $"Timeline so far: [{string.Join("; ", Timeline)}]");
            }
        }

        /// <summary>
        /// Waits until the timeline contains an entry matching
        /// <paramref name="match"/>, or fails the test on timeout.
        /// </summary>
        public Task WaitForTimelineEntryAsync(
            Func<string, bool> match, string because, TimeSpan? timeout = null)
            => WaitForTimelineAsync(tl => tl.Any(match), because, timeout);

        /// <summary>
        /// Snapshot of the timeline under its lock (safe to assert on
        /// while the renderer thread may still be recording).
        /// </summary>
        public string[] TimelineSnapshot()
        {
            lock (Timeline)
            {
                return Timeline.ToArray();
            }
        }

        /// <summary>
        /// Snapshot of the dispatched mark-read commands under the list's
        /// lock.
        /// </summary>
        public MarkNotificationAsReadCommand[] MarkedReadSnapshot()
        {
            lock (MarkedRead)
            {
                return MarkedRead.ToArray();
            }
        }
    }

    /// <summary>
    /// Builds the bUnit context with everything BOTH notification pages
    /// inject: the recording bus, the recording NavigationManager (bUnit's
    /// default TestNavigationManager THROWS on forceLoad — the AppUpdate
    /// tap needs one that records), the identity localizer, the singleton
    /// ToastService override, and a real NotificationRefreshService over
    /// bUnit's loose JS runtime.
    /// </summary>
    /// <param name="rows">The canned first page of notifications.</param>
    /// <param name="baseUri">The current page URI ("/Notifications"
    /// desktop, "/m/Notifications" mobile) — also the force-load target
    /// the AppUpdate tap navigates to.</param>
    private static Harness Setup(IReadOnlyList<NotificationDto> rows, string baseUri)
    {
        var ctx = ComponentTestSetup.CreateRadzenEnabledContext();

        // Identity localizers for each page twin's own Loc[...] lookups.
        ComponentTestSetup.AddIdentityLocalizer<Notifications>(ctx);
        ComponentTestSetup.AddIdentityLocalizer<MobileNotifications>(ctx);

        var errLoc = Substitute.For<IStringLocalizer<UnexpectedErrorMessages>>();
        errLoc[Arg.Any<string>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        ctx.Services.AddSingleton(errLoc);
        ctx.Services.AddSingleton(new ErrorDisplayService(errLoc));

        // AddRadzenComponents registers NotificationService as SCOPED, which
        // bUnit (root provider, no scope) cannot resolve — override with a
        // singleton instance so the ToastService factory resolves (same as
        // the SalesPagePendingChipTests harness).
        ctx.Services.AddSingleton(new Radzen.NotificationService());
        ctx.Services.AddSingleton(sp => new ToastService(
            sp.GetRequiredService<Radzen.NotificationService>(),
            sp.GetRequiredService<ErrorDisplayService>()));

        // The page subscribes to RefreshReceived on mount and unsubscribes
        // on dispose — a real instance over bUnit's loose JS runtime is the
        // cheapest faithful wiring (no JS call happens at page level).
        ctx.Services.AddSingleton(new NotificationRefreshService(
            ctx.JSInterop.JSRuntime,
            NullLogger<NotificationRefreshService>.Instance));

        var timeline = new List<string>();
        var markedRead = new List<MarkNotificationAsReadCommand>();
        var gate = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The recording bus. Every dispatch appends to the shared timeline
        // synchronously (the Returns callbacks run inside the awaited
        // InvokeAsync call, so the timeline order equals the page's await
        // order). The mark-read command returns the GATE task — the test
        // holds the round-trip open to prove the handler awaits it.
        var bus = Substitute.For<IMessageBus>();

        bus.InvokeAsync<PaginatedResult<NotificationDto>>(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var query = ci.ArgAt<object>(0).Should().BeOfType<GetNotificationsForUserQuery>().Subject;
                lock (timeline) { timeline.Add("bus:GetNotificationsForUserQuery"); }
                return new PaginatedResult<NotificationDto>(rows, rows.Count, query.PageNumber, query.PageSize);
            });

        bus.InvokeAsync<int>(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                ci.ArgAt<object>(0).Should().BeOfType<GetUnreadNotificationCountQuery>();
                lock (timeline) { timeline.Add("bus:GetUnreadNotificationCountQuery"); }
                return rows.Count(r => r.IsUnread);
            });

        bus.InvokeAsync<Result>(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var command = ci.ArgAt<object>(0).Should().BeOfType<MarkNotificationAsReadCommand>().Subject;
                lock (timeline) { timeline.Add($"bus:MarkNotificationAsReadCommand({command.NotificationId})"); }
                lock (markedRead) { markedRead.Add(command); }
                return gate.Task;
            });

        ctx.Services.AddSingleton(bus);

        // bUnit's default TestNavigationManager throws NotSupportedException
        // on forceLoad: true — the AppUpdate tap REQUIRES forceLoad, so
        // replace it with a recorder that participates in the timeline.
        var nav = new RecordingNavigationManager(baseUri, timeline);
        ctx.Services.RemoveAll<NavigationManager>();
        ctx.Services.AddSingleton<NavigationManager>(nav);

        return new Harness(ctx, gate, timeline, markedRead);
    }

    private static NotificationDto MakeNotification(
        NotificationKind kind,
        bool unread = true,
        Guid? saleId = null,
        string title = "Test title",
        string message = "Test message") => new()
    {
        Id = Guid.NewGuid(),
        Kind = kind,
        SaleId = saleId,
        Title = title,
        Message = message,
        CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
        ReadAtUtc = unread ? null : DateTimeOffset.UtcNow.AddMinutes(-1)
    };

    /// <summary>
    /// Runs the tap click WITHOUT awaiting the handler (the handler blocks
    /// on the gate), waits until the mark-read command has reached the fake
    /// bus, and asserts the regression pin: NO navigation may be requested
    /// while the read-state round-trip is still in flight. Then releases
    /// the gate, waits for the handler's follow-up effect
    /// (<paramref name="waitForFollowUp"/>), and runs
    /// <paramref name="assertFinal"/> on the final timeline snapshot.
    /// </summary>
    /// <param name="h">The recording harness.</param>
    /// <param name="row">The tapped notification (for the id assertion).
    /// </param>
    /// <param name="clickAsync">Invokes the row tap — e.g.
    /// <code>() => cut.InvokeAsync(() => cut.Find("button.notif-card").Click())</code>
    /// (the element is re-Found inside the delegate per the bUnit
    /// discipline; note bUnit's InvokeAsync completes when the CLICK
    /// DISPATCH completes — the async event handler continues on the
    /// renderer's sync context, which is why the follow-up effect is
    /// wait-pooled rather than assumed).</param>
    /// <param name="waitForFollowUp">The timeline condition that proves the
    /// handler completed its post-command work (navigation recorded /
    /// refresh query dispatched).</param>
    /// <param name="assertFinal">Final assertions on the timeline
    /// snapshot.</param>
    private static async Task TapAndAssertGatedDispatchAsync(
        Harness h,
        NotificationDto row,
        Func<Task> clickAsync,
        Func<IReadOnlyList<string>, bool> waitForFollowUp,
        string followUpBecause,
        Action<string[]> assertFinal)
    {
        // Act (1) — tap the row; with the fixed code the handler is now
        // blocked on the gated round-trip.
        var clickTask = clickAsync();

        // The dispatch reaches the fake bus synchronously inside the click.
        await h.WaitForTimelineEntryAsync(
            t => t.StartsWith("bus:MarkNotificationAsReadCommand", StringComparison.Ordinal),
            "the tap must dispatch the mark-read command for the tapped row");
        h.MarkedReadSnapshot().Should().ContainSingle(c => c.NotificationId == row.Id);

        // THE REGRESSION PIN — while the read-state round-trip is in
        // flight, NO navigation may be requested. Under the old
        // fire-and-forget Task.Run code the navigation was requested
        // immediately (the handler never blocked), letting the force-load
        // teardown race the in-flight dispatch — the production
        // "Authentication required" incident.
        await Task.Delay(300); // let any (buggy) un-awaited navigation fire
        h.TimelineSnapshot().Should().NotContain(
            t => t.StartsWith("nav:", StringComparison.Ordinal),
            "the tap must not navigate until the mark-read round-trip completes — navigating first lets the force-load circuit teardown race the in-flight dispatch and lose the ambient user");

        // Act (2) — complete the round-trip; the handler proceeds to its
        // follow-up (navigation / refresh), which lands on the renderer's
        // sync context asynchronously — wait for it from the test thread.
        h.Gate.SetResult(Result.Success());
        await clickTask;
        await h.WaitForTimelineAsync(waitForFollowUp, followUpBecause);

        // Assert — the final timeline.
        assertFinal(h.TimelineSnapshot());
    }

    // ──────────────────────────────────────────────────────────────
    // DESKTOP (Notifications.razor)
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Desktop_TapAppUpdate_MarksReadBeforeForceReloadNavigation()
    {
        // Arrange — one UNREAD app-update notification (the exact shape
        // from the production incident).
        var row = MakeNotification(NotificationKind.AppUpdate);
        using var h = Setup(new[] { row }, "/Notifications");
        var cut = h.Context.Render<Notifications>();
        cut.WaitForAssertion(
            () => cut.FindAll("button.notif-card").Should().HaveCount(1),
            TimeSpan.FromSeconds(10));

        // Act + Assert — dispatch gated; no navigation while in flight;
        // then the same-URI force-load reload.
        await TapAndAssertGatedDispatchAsync(h, row,
            () => cut.InvokeAsync(() => cut.Find("button.notif-card").Click()),
            tl => tl.Any(t => t.StartsWith("nav:", StringComparison.Ordinal)),
            "the app-update tap must request the force-load reload once the round-trip completes",
            timeline =>
        {
            var navIndex = Array.FindIndex(timeline, t => t.StartsWith("nav:", StringComparison.Ordinal));
            navIndex.Should().BeGreaterThanOrEqualTo(0, "the app-update tap must request the force-load reload");
            timeline[navIndex].Should().Be("nav:http://localhost/Notifications|forceLoad=True",
                "the app-update tap force-reloads the current page so the browser picks up the new assets");
        });
    }

    [Fact]
    public async Task Desktop_TapSaleNotification_MarksReadBeforeNavigatingToSale()
    {
        // Arrange — one unread sale-submitted notification with a sale link.
        var saleId = Guid.NewGuid();
        var row = MakeNotification(NotificationKind.SaleSubmitted, saleId: saleId);
        using var h = Setup(new[] { row }, "/Notifications");
        var cut = h.Context.Render<Notifications>();
        cut.WaitForAssertion(
            () => cut.FindAll("button.notif-card").Should().HaveCount(1),
            TimeSpan.FromSeconds(10));

        // Act + Assert — dispatch gated; no navigation while in flight;
        // then the (non-force) navigation to the sale detail page.
        await TapAndAssertGatedDispatchAsync(h, row,
            () => cut.InvokeAsync(() => cut.Find("button.notif-card").Click()),
            tl => tl.Any(t => t.StartsWith("nav:", StringComparison.Ordinal)),
            "the sale tap must navigate to the sale detail page once the round-trip completes",
            timeline =>
        {
            var navIndex = Array.FindIndex(timeline, t => t.StartsWith("nav:", StringComparison.Ordinal));
            navIndex.Should().BeGreaterThanOrEqualTo(0);
            // The page passes the sale route as a RELATIVE uri — the raw
            // string reaches NavigateToCore unnormalized (the real
            // NavigationManager resolves it against BaseUri at the
            // boundary).
            timeline[navIndex].Should().Be($"nav:/Sales/{saleId}|forceLoad=False");
        });
    }

    [Fact]
    public async Task Desktop_TapBroadcast_MarksReadThenRefreshesList()
    {
        // Arrange — one unread admin broadcast (no navigation target: the
        // tap dismisses it in place and refreshes the list so the unread
        // dot + bell badge update).
        var row = MakeNotification(NotificationKind.Broadcast);
        using var h = Setup(new[] { row }, "/Notifications");
        var cut = h.Context.Render<Notifications>();
        cut.WaitForAssertion(
            () => cut.FindAll("button.notif-card").Should().HaveCount(1),
            TimeSpan.FromSeconds(10));

        // Act + Assert — dispatch gated; nothing while in flight; then the
        // in-place list refresh (a follow-up query AFTER the command), and
        // NO navigation away.
        await TapAndAssertGatedDispatchAsync(h, row,
            () => cut.InvokeAsync(() => cut.Find("button.notif-card").Click()),
            tl => tl.Count(t => t == "bus:GetNotificationsForUserQuery") >= 2,
            "the broadcast tap must refresh the list once the round-trip completes",
            timeline =>
        {
            var commandIndex = Array.FindIndex(
                timeline, t => t.StartsWith("bus:MarkNotificationAsReadCommand", StringComparison.Ordinal));
            var refreshIndex = Array.FindIndex(
                timeline, commandIndex + 1, timeline.Length - commandIndex - 1,
                (string t) => t == "bus:GetNotificationsForUserQuery");
            refreshIndex.Should().BeGreaterThanOrEqualTo(0,
                "the broadcast tap refreshes the list AFTER the read-state is persisted");
            timeline.Should().NotContain(t => t.StartsWith("nav:", StringComparison.Ordinal),
                "a broadcast tap has no navigation target");
        });
    }

    // ──────────────────────────────────────────────────────────────
    // MOBILE (MobileNotifications.razor) — the identical twin fix
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mobile_TapAppUpdate_MarksReadBeforeForceReloadNavigation()
    {
        // Arrange — one unread app-update notification on the mobile twin.
        var row = MakeNotification(NotificationKind.AppUpdate);
        using var h = Setup(new[] { row }, "/m/Notifications");
        var cut = h.Context.Render<MobileNotifications>();
        cut.WaitForAssertion(
            () => cut.FindAll("button.m-notif-card").Should().HaveCount(1),
            TimeSpan.FromSeconds(10));

        // Act + Assert — same gated-dispatch pin as the desktop twin.
        await TapAndAssertGatedDispatchAsync(h, row,
            () => cut.InvokeAsync(() => cut.Find("button.m-notif-card").Click()),
            tl => tl.Any(t => t.StartsWith("nav:", StringComparison.Ordinal)),
            "the mobile app-update tap must request the force-load reload once the round-trip completes",
            timeline =>
        {
            var navIndex = Array.FindIndex(timeline, t => t.StartsWith("nav:", StringComparison.Ordinal));
            navIndex.Should().BeGreaterThanOrEqualTo(0);
            timeline[navIndex].Should().Be("nav:http://localhost/m/Notifications|forceLoad=True");
        });
    }

    [Fact]
    public async Task Mobile_TapSaleNotification_MarksReadBeforeNavigatingToSale()
    {
        // Arrange — one unread sale notification with a sale link.
        var saleId = Guid.NewGuid();
        var row = MakeNotification(NotificationKind.SaleSubmitted, saleId: saleId);
        using var h = Setup(new[] { row }, "/m/Notifications");
        var cut = h.Context.Render<MobileNotifications>();
        cut.WaitForAssertion(
            () => cut.FindAll("button.m-notif-card").Should().HaveCount(1),
            TimeSpan.FromSeconds(10));

        // Act + Assert — dispatch gated; no navigation while in flight;
        // then the mobile sale route (relative, non-force).
        await TapAndAssertGatedDispatchAsync(h, row,
            () => cut.InvokeAsync(() => cut.Find("button.m-notif-card").Click()),
            tl => tl.Any(t => t.StartsWith("nav:", StringComparison.Ordinal)),
            "the mobile sale tap must navigate to the sale detail page once the round-trip completes",
            timeline =>
        {
            var navIndex = Array.FindIndex(timeline, t => t.StartsWith("nav:", StringComparison.Ordinal));
            navIndex.Should().BeGreaterThanOrEqualTo(0);
            timeline[navIndex].Should().Be($"nav:/m/Sales/{saleId}|forceLoad=False");
        });
    }

    /// <summary>
    /// A <see cref="NavigationManager"/> that records every
    /// <c>NavigateTo</c> (uri + forceLoad) into the shared timeline
    /// instead of navigating. Needed because bUnit's built-in
    /// TestNavigationManager rejects <c>forceLoad: true</c> and the
    /// AppUpdate tap depends on it.
    /// </summary>
    private sealed class RecordingNavigationManager : NavigationManager
    {
        private readonly List<string> _timeline;

        public RecordingNavigationManager(string relativeUri, List<string> timeline)
        {
            _timeline = timeline;
            Initialize("http://localhost/", $"http://localhost{relativeUri}");
        }

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
            lock (_timeline) { _timeline.Add($"nav:{uri}|forceLoad={forceLoad}"); }
        }
    }
}
