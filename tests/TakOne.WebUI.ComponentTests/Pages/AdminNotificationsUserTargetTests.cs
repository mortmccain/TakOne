using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Customers.DTOs;
using TakOne.Application.Customers.Queries.GetAllCustomerGroups;
using TakOne.Application.Notifications.Commands.SendBroadcastNotification;
using TakOne.Application.Notifications.DTOs;
using TakOne.Application.Notifications.Queries.GetBroadcastNotifications;
using TakOne.Application.Resources;
using TakOne.Application.Users.DTOs;
using TakOne.Application.Users.Queries.GetUsersPaginated;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;
using TakOne.WebUI.Components.MobilePages.MobileAdminNotifications;
using TakOne.WebUI.Components.Pages.AdminNotifications;
using TakOne.WebUI.Services;
using Wolverine;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Pages;

/// <summary>
/// bUnit tests for the broadcast-compose "To User" flow on BOTH the
/// desktop (<see cref="AdminNotifications"/>) and mobile
/// (<see cref="MobileAdminNotifications"/>) admin pages — the recipient
/// search + the send guard added to fix the owner-reported bug:
/// "you can not type the personal Id halfway and it showing the similar
/// ones… when you type the whole id or the name, you get this error
/// (87DKG64)".
/// </summary>
/// <remarks>
/// <para>
/// ROOT CAUSE under test (all three behaviors below pin the fix):
/// <list type="number">
///   <item>The desktop results lived inside a CLOSED RadzenDropDown, so
///     "the similar ones" were invisible while typing — replaced by a
///     visible results list (asserted indirectly: the mobile list is
///     markup-asserted; the desktop list shares the SelectUser
///     path).</item>
///   <item>Typing the FULL id/name left the search box's text in
///     <c>_userSearchTerm</c> but <c>_targetUserId</c> null; Send then
///     dispatched <c>Scope=User, TargetUserId=null</c>, Wolverine's
///     FluentValidation short-circuit THREW, and the page's generic catch
///     masked it as "unexpected error 87DKG64". Fix 1: auto-select when
///     the typed term exactly (case-insensitive) matches one user's
///     WorkerId or FullName. Fix 2: a local send guard that rejects
///     User-scoped sends with no selection — the command is NEVER
///     dispatched.</item>
/// </list>
/// </para>
/// <para>
/// <b>Render note.</b> Both pages carry route-level
/// <c>[Authorize(Policy = "AdminOnly")]</c>, which is enforced by the
/// Router — <c>ctx.Render&lt;T&gt;</c> bypasses it (the same discipline as
/// every other page test in this suite). The desktop guard test does not
/// need the debounced search box at all; the mobile page's search is a
/// plain <c>input</c> with a C#-side 300 ms debounce, driven through
/// <c>Input(...)</c> + <c>WaitForState</c>.
/// </para>
/// </remarks>
public class AdminNotificationsUserTargetTests
{
    // ── Shared fake-bus harness ─────────────────────────────────────────

    /// <summary>
    /// Builds a fake <see cref="IMessageBus"/> that answers the broadcast
    /// pages' three load queries with empty payloads and RECORDS every
    /// dispatched <see cref="SendBroadcastNotificationCommand"/>.
    /// </summary>
    private static (IMessageBus bus, List<SendBroadcastNotificationCommand> sent) BuildBus(
        Func<GetUsersPaginatedQuery, List<UserListItemDto>>? usersProvider = null)
    {
        var sent = new List<SendBroadcastNotificationCommand>();
        var bus = Substitute.For<IMessageBus>();

        bus.InvokeAsync<Result<List<CustomerGroupListItemDto>>>(
                Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(Result<List<CustomerGroupListItemDto>>.Success(new List<CustomerGroupListItemDto>()));

        bus.InvokeAsync<PaginatedResult<BroadcastNotificationDto>>(
                Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<BroadcastNotificationDto>(
                new List<BroadcastNotificationDto>(), 0, 1, 10));

        var provider = usersProvider ?? (_ => new List<UserListItemDto>());
        bus.InvokeAsync<PaginatedResult<UserListItemDto>>(
                Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var query = ci.ArgAt<object>(0).Should().BeOfType<GetUsersPaginatedQuery>().Subject;
                return new PaginatedResult<UserListItemDto>(
                    provider(query), 1, query.PageNumber, query.PageSize);
            });

        bus.InvokeAsync<Result<int>>(
                Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var cmd = ci.ArgAt<object>(0).Should().BeOfType<SendBroadcastNotificationCommand>().Subject;
                lock (sent)
                {
                    sent.Add(cmd);
                }
                return Result<int>.Success(1);
            });

        return (bus, sent);
    }

    /// <summary>
    /// Registers the shared page services (localizer stub returning keys,
    /// real ToastService over a spy-friendly Radzen notification service,
    /// ErrorDisplayService, logger) on the context.
    /// </summary>
    private static void AddSharedServices<TPage>(BunitContext ctx) where TPage : class
    {
        var loc = Substitute.For<IStringLocalizer<TPage>>();
        loc[Arg.Any<string>()].Returns(ci =>
            new LocalizedString(ci.ArgAt<string>(0), ci.ArgAt<string>(0), false));
        loc[Arg.Any<string>(), Arg.Any<object[]>()].Returns(ci =>
        {
            var args = ci.ArgAt<object[]>(1) ?? Array.Empty<object>();
            return new LocalizedString(ci.ArgAt<string>(0), string.Join("|", args), false);
        });
        ctx.Services.AddSingleton(loc);

        var errLoc = Substitute.For<IStringLocalizer<UnexpectedErrorMessages>>();
        errLoc[Arg.Any<string>()].Returns(ci =>
            new LocalizedString(ci.ArgAt<string>(0), ci.ArgAt<string>(0), false));
        ctx.Services.AddSingleton(errLoc);
        ctx.Services.AddSingleton(new ErrorDisplayService(errLoc));
        ctx.Services.AddSingleton(new Radzen.NotificationService());
        ctx.Services.AddSingleton(sp => new ToastService(
            sp.GetRequiredService<Radzen.NotificationService>(),
            sp.GetRequiredService<ErrorDisplayService>()));
        ctx.Services.AddSingleton(Substitute.For<ILogger<TPage>>());
    }

    private static UserListItemDto MakeUser(string workerId, string fullName) => new()
    {
        Id = Guid.NewGuid(),
        WorkerId = workerId,
        FullName = fullName,
        IsActive = true
    };

    // ── Desktop page: the send guard ────────────────────────────────────

    [Fact]
    public async Task Desktop_UserScopeSend_WithoutSelection_DoesNotDispatchCommand()
    {
        // Arrange — an admin composes a valid title+message, picks the
        // "To User" scope, but never resolves a recipient (the exact
        // pre-fix scenario that produced 87DKG64). The guard must reject
        // the send LOCALLY — no SendBroadcastNotificationCommand may reach
        // the bus (Wolverine's validator would throw and the generic
        // catch would mask it as an unexpected error).
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        AddSharedServices<AdminNotifications>(ctx);
        ComponentTestSetup.AddAuthenticatedUser(ctx, "admin", Roles.Admin);
        var (bus, sent) = BuildBus();
        ctx.Services.AddSingleton(bus);

        var cut = ctx.Render<AdminNotifications>();

        // Fill Title + Message (RadzenTextBox / RadzenTextArea — Change
        // triggers the ValueChanged binding, per the RestockProductDialog
        // test precedent).
        cut.Find("#bc-title").Change("Quarterly reminder");
        cut.Find("#bc-msg").Change("Please submit your orders by Thursday.");

        // Switch to the "To User" scope (segmented button — the identity
        // localizer renders the key as its text).
        await cut.InvokeAsync(() =>
            cut.FindAll("button").Single(b => b.TextContent.Contains("ScopeUser")).Click());

        // Act — Send with NO resolved recipient.
        await cut.InvokeAsync(() =>
            cut.FindAll("button").Single(b => b.TextContent.Contains("SendButton")).Click());

        // Assert — the guard rejected the send before any dispatch.
        sent.Should().BeEmpty(
            "a User-scoped broadcast with no resolved recipient must never reach the bus");
    }

    // ── Mobile page: auto-select + guard + happy path ───────────────────

    private static BunitContext SetupMobile(
        Func<GetUsersPaginatedQuery, List<UserListItemDto>>? usersProvider = null)
    {
        var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        AddSharedServices<MobileAdminNotifications>(ctx);
        ComponentTestSetup.AddAuthenticatedUser(ctx, "admin", Roles.Admin);
        var (bus, _) = BuildBus(usersProvider);
        ctx.Services.AddSingleton(bus);
        return ctx;
    }

    private static async Task<IRenderedComponent<MobileAdminNotifications>> RenderMobileAsync(
        BunitContext ctx)
    {
        var cut = ctx.Render<MobileAdminNotifications>();
        // The page starts in its skeleton state; OnInitializedAsync loads
        // groups + the first audit page, then the compose form renders.
        await cut.WaitForStateAsync(
            () => cut.FindAll("button").Any(b => b.TextContent.Contains("ScopeUser")),
            TimeSpan.FromSeconds(5));
        return cut;
    }

    [Fact]
    public async Task Mobile_TypingFullWorkerId_AutoSelectsRecipient()
    {
        // Arrange — the search returns TWO partial matches for "EMP", but
        // only ONE exact WorkerId match: typing the COMPLETE id must
        // resolve the recipient without a tap (the chip appears).
        var ali = MakeUser("EMP-0001", "Ali Ahmadi");
        using var ctx = SetupMobile(q =>
            new List<UserListItemDto> { ali, MakeUser("EMP-0002", "Reza Karimi") });
        var cut = await RenderMobileAsync(ctx);

        await cut.InvokeAsync(() =>
            cut.FindAll("button").Single(b => b.TextContent.Contains("ScopeUser")).Click());
        await cut.InvokeAsync(() => cut.Find("#bc-user-search").Input("EMP-0001"));

        // Act — the 300 ms C# debounce fires the query; the exact match
        // auto-selects and the chip replaces the results list.
        await cut.WaitForStateAsync(
            () => cut.FindAll(".m-broadcast-selected-name").Any(),
            TimeSpan.FromSeconds(5));

        // Assert — the chip carries the resolved user's display name.
        cut.Find(".m-broadcast-selected-name").TextContent.Should().Contain("Ali Ahmadi");
    }

    [Fact]
    public async Task Mobile_AmbiguousFullName_DoesNotAutoSelect()
    {
        // Arrange — TWO users share the exact full name: an exact-text
        // match is ambiguous and must NOT silently pick one of them; the
        // results list stays visible and the admin picks explicitly.
        var twin1 = MakeUser("EMP-0011", "Sara Moradi");
        var twin2 = MakeUser("EMP-0012", "Sara Moradi");
        using var ctx = SetupMobile(q => new List<UserListItemDto> { twin1, twin2 });
        var cut = await RenderMobileAsync(ctx);

        await cut.InvokeAsync(() =>
            cut.FindAll("button").Single(b => b.TextContent.Contains("ScopeUser")).Click());
        await cut.InvokeAsync(() => cut.Find("#bc-user-search").Input("Sara Moradi"));

        // Act/Assert — results render, but no chip (no auto-select) and
        // therefore no resolved target.
        await cut.WaitForStateAsync(
            () => cut.FindAll(".m-broadcast-user-row").Any(),
            TimeSpan.FromSeconds(5));
        cut.FindAll(".m-broadcast-selected-name").Should().BeEmpty(
            "an ambiguous full-name match must not auto-select");
    }

    [Fact]
    public async Task Mobile_UserScopeSend_WithoutSelection_DoesNotDispatchCommand()
    {
        // Arrange — the mobile twin of the desktop guard test.
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        AddSharedServices<MobileAdminNotifications>(ctx);
        ComponentTestSetup.AddAuthenticatedUser(ctx, "admin", Roles.Admin);
        var (bus, sent) = BuildBus();
        ctx.Services.AddSingleton(bus);
        var cut = await RenderMobileAsync(ctx);

        await cut.InvokeAsync(() =>
            cut.FindAll("button").Single(b => b.TextContent.Contains("ScopeUser")).Click());
        await cut.InvokeAsync(() => cut.Find("#bc-title").Input("Title"));
        await cut.InvokeAsync(() => cut.Find("#bc-message").Input("Body"));

        // Act — Send with a typed-but-unresolved recipient.
        await cut.InvokeAsync(() =>
            cut.FindAll("button").Single(b => b.TextContent.Contains("SendButton")).Click());

        // Assert — rejected locally, nothing dispatched.
        sent.Should().BeEmpty(
            "a User-scoped broadcast with no resolved recipient must never reach the bus");
    }

    [Fact]
    public async Task Mobile_Send_AfterAutoSelection_DispatchesCommandWithTargetUserId()
    {
        // Arrange — the full happy path that used to fail with 87DKG64:
        // type the complete id (auto-select), fill the form, Send. The
        // dispatched command must carry the RESOLVED user's id.
        var ali = MakeUser("EMP-0001", "Ali Ahmadi");
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        AddSharedServices<MobileAdminNotifications>(ctx);
        ComponentTestSetup.AddAuthenticatedUser(ctx, "admin", Roles.Admin);
        var (bus, sent) = BuildBus(q => new List<UserListItemDto> { ali });
        ctx.Services.AddSingleton(bus);
        var cut = await RenderMobileAsync(ctx);

        await cut.InvokeAsync(() =>
            cut.FindAll("button").Single(b => b.TextContent.Contains("ScopeUser")).Click());
        await cut.InvokeAsync(() => cut.Find("#bc-user-search").Input("EMP-0001"));
        await cut.WaitForStateAsync(
            () => cut.FindAll(".m-broadcast-selected-name").Any(),
            TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut.Find("#bc-title").Input("Quarterly reminder"));
        await cut.InvokeAsync(() => cut.Find("#bc-message").Input("Please submit by Thursday."));

        // Act
        await cut.InvokeAsync(() =>
            cut.FindAll("button").Single(b => b.TextContent.Contains("SendButton")).Click());

        // Assert — the command reached the bus with the resolved target
        // (the pre-fix flow never got this far: it dispatched a null
        // TargetUserId and died in validation).
        await cut.WaitForAssertionAsync(
            () => sent.Should().ContainSingle(c =>
                c.TargetUserId == ali.Id
                && c.Title == "Quarterly reminder"
                && c.Message == "Please submit by Thursday."),
            TimeSpan.FromSeconds(5));
    }
}
