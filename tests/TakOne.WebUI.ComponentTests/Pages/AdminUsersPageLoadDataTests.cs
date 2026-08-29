using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Customers.DTOs;
using TakOne.Application.Customers.Queries.GetAllCustomerGroups;
using TakOne.Application.Resources;
using TakOne.Application.Users.DTOs;
using TakOne.Application.Users.Queries.GetUsersPaginated;
using TakOne.Domain.Users;
using TakOne.SharedKernel.Common;
using TakOne.WebUI.Components.Pages.AdminUsers;
using TakOne.WebUI.Services;
using Wolverine;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Pages;

/// <summary>
/// Page-level bUnit test for the AdminUsers grid's Radzen LoadData
/// (server-driven paging) wiring — Round 5's riskiest glue. Mirrors
/// <see cref="SalesPageLoadDataTests"/> from Round 4.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHAT THIS PROVES</b> (that the per-layer tests cannot): the page
/// actually MOUNTS the grid with <c>Data=null</c> (the pinned Radzen
/// contract for LoadData-on-init), the grid's initial LoadData reaches
/// <c>IMessageBus.InvokeAsync</c> with a correctly-shaped query, and the
/// returned page flows back into the grid via <c>Data</c> + <c>Count</c>
/// (rows render; the pager shows the server-side total).
/// </para>
/// <para>
/// <b>WIRING</b>: the IMessageBus is an NSubstitute fake returning a
/// canned page (plus an empty group list for the filter-dropdown load);
/// every other dependency uses the same real/stub mix the rest of this
/// project's component tests use.
/// </para>
/// </remarks>
public class AdminUsersPageLoadDataTests
{
    private static List<UserListItemDto> MakeUsers(int count) =>
        Enumerable.Range(1, count).Select(i => new UserListItemDto
        {
            Id = Guid.NewGuid(),
            WorkerId = $"EMP-{i:000}",
            FullName = $"User {i:000}",
            Gender = i % 2 == 0 ? Gender.Female : Gender.Male,
            GroupId = null,
            GroupName = null,
            IsActive = true,
            Roles = new List<string> { Roles.Customer }
        }).ToList();

    private static (TestContext ctx, IMessageBus bus) Setup(int totalCount, List<UserListItemDto> page)
    {
        var ctx = ComponentTestSetup.CreateRadzenEnabledContext();

        // Identity localizer for the page's own Loc[...] lookups.
        ComponentTestSetup.AddIdentityLocalizer<AdminUsers>(ctx);

        // ErrorDisplayService + ToastService (real instances; the localizer
        // is stubbed so the error paths resolve without resx infrastructure).
        var errLoc = Substitute.For<IStringLocalizer<UnexpectedErrorMessages>>();
        errLoc[Arg.Any<string>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        ctx.Services.AddSingleton(errLoc);
        ctx.Services.AddSingleton(new ErrorDisplayService(errLoc));
        // AddRadzenComponents registers NotificationService as SCOPED,
        // which bUnit (root provider, no scope) cannot resolve — override
        // with a singleton instance so the ToastService factory resolves.
        ctx.Services.AddSingleton(new Radzen.NotificationService());
        ctx.Services.AddSingleton(sp => new ToastService(
            sp.GetRequiredService<Radzen.NotificationService>(),
            sp.GetRequiredService<ErrorDisplayService>()));

        // AdminUsers injects ILogger<AdminUsers> (dialog-open failure path).
        ctx.Services.AddSingleton(Substitute.For<ILogger<AdminUsers>>());

        // AdminUsers injects Radzen.DialogService (deactivate confirm) —
        // the spy substitute covers the injection without opening dialogs.
        ComponentTestSetup.AddDialogServiceSpy(ctx);

        // Admin user (the page resolves roles via the auth provider).
        ComponentTestSetup.AddAuthenticatedUser(ctx, "tester", Roles.Admin);

        // The fake bus: canned users page + empty group list (the filter
        // dropdown's one-time load in OnInitializedAsync).
        var bus = Substitute.For<IMessageBus>();
        bus.InvokeAsync<PaginatedResult<UserListItemDto>>(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var query = ci.ArgAt<object>(0).Should().BeOfType<GetUsersPaginatedQuery>().Subject;
                return new PaginatedResult<UserListItemDto>(page, totalCount, query.PageNumber, query.PageSize);
            });
        bus.InvokeAsync<Result<List<CustomerGroupListItemDto>>>(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(Result<List<CustomerGroupListItemDto>>.Success(new List<CustomerGroupListItemDto>()));
        ctx.Services.AddSingleton(bus);

        return (ctx, bus);
    }

    [Fact]
    public async Task AdminUsers_Mount_FiresLoadData_AndRendersPageWithServerTotal()
    {
        // Arrange — 45 "server" rows, page 1 of 20.
        var (ctx, bus) = Setup(totalCount: 45, page: MakeUsers(20));
        using (ctx)
        {
            // Act — mount the page. The grid's LoadData-on-init fires
            // during the initial render lifecycle.
            var cut = ctx.RenderComponent<AdminUsers>();

            // Assert — the load settles: 20 rows render, the pager
            // reflects the SERVER total (45), not the local page size.
            cut.WaitForAssertion(
                () => cut.FindAll(".worker-id-cell").Should().HaveCount(20),
                TimeSpan.FromSeconds(10));

            // The pager reflects the SERVER total: 45 rows / 20 per page
            // = 3 page buttons. (Asserting on the pager buttons rather
            // than the summary text — the identity localizer stub returns
            // raw keys for PagingSummaryFormat, so the summary's literal
            // text isn't stable under test.)
            cut.WaitForAssertion(
                () => cut.FindAll("button.rz-pager-page")
                    .Should().HaveCount(3),
                TimeSpan.FromSeconds(5));

            // The dispatched query was the initial page with the default
            // FullName-ascending sort (null SortBy + not descending).
            await bus.Received(1).InvokeAsync<PaginatedResult<UserListItemDto>>(
                Arg.Is<GetUsersPaginatedQuery>(q => q.PageNumber == 1 && q.PageSize == 20),
                Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task AdminUsers_PagerPageTwoClick_DispatchesSecondPage()
    {
        // Arrange
        var (ctx, bus) = Setup(totalCount: 45, page: MakeUsers(20));
        using (ctx)
        {
            var cut = ctx.RenderComponent<AdminUsers>();
            cut.WaitForAssertion(
                () => cut.FindAll(".worker-id-cell").Should().HaveCount(20),
                TimeSpan.FromSeconds(10));

            // Act — click the pager's "2".
            var pageTwo = cut.FindAll("button.rz-pager-page")
                .First(e => e.TextContent.Trim() == "2");
            await cut.InvokeAsync(() => pageTwo.Click());

            // Assert — a second query dispatched at PageNumber=2
            // (Skip=20 / PageSize=20 → page 2).
            await bus.Received(1).InvokeAsync<PaginatedResult<UserListItemDto>>(
                Arg.Is<GetUsersPaginatedQuery>(q => q.PageNumber == 2),
                Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public void AdminUsers_EmptyServerResult_RendersFreshEmptyState()
    {
        // Arrange — zero users on the server (fresh install).
        var (ctx, bus) = Setup(totalCount: 0, page: new List<UserListItemDto>());
        using (ctx)
        {
            // Act
            var cut = ctx.RenderComponent<AdminUsers>();

            // Assert — the fresh-install empty state (no filters were
            // active): the "no users yet" card, NOT the filtered-empty
            // variant, and NOT a permanently-spinning skeleton.
            cut.WaitForAssertion(
                () => cut.Markup.Should().Contain("EmptyTitle"),
                TimeSpan.FromSeconds(10));
            cut.Markup.Should().NotContain("EmptyTitleFiltered",
                "no filter was active — this is the fresh-install variant");
        }
    }
}
