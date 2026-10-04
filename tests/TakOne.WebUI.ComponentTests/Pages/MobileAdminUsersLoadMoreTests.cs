using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Resources;
using TakOne.Application.Users.DTOs;
using TakOne.Application.Users.Queries.GetUsersPaginated;
using TakOne.Domain.Users;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;
using TakOne.WebUI.Components.MobilePages.MobileAdminUsers;
using TakOne.WebUI.Services;
using Wolverine;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Pages;

/// <summary>
/// Shared bUnit harness for mounting the FULL mobile admin user-directory
/// page (QA-12 round — the first page-level coverage MobileAdminUsers
/// has ever had).
/// </summary>
/// <remarks>
/// <para>
/// The page dispatches ONE query type (<see cref="GetUsersPaginatedQuery"/>)
/// — the fake bus records every dispatch in order and answers through a
/// per-page provider (the sequence-based discipline of
/// <see cref="MobileProductsPageTestHarness"/>).
/// </para>
/// <para>
/// Roles are wired through the claims-based
/// <see cref="ComponentTestSetup.AddAuthenticatedUser"/> — the page
/// resolves <c>_isAdmin</c>/<c>_isManagerOnly</c> from the
/// AuthenticationStateProvider's principal, and renders no AuthorizeView
/// (the route-level [Authorize(Policy)] is enforced by the Router, which
/// RenderComponent bypasses).
/// </para>
/// <para>
/// The localizer stub ALSO configures the format-args indexer (the count
/// line uses <c>Loc["ShowingCount", a, b, c]</c>).
/// </para>
/// </remarks>
internal static class MobileAdminUsersPageTestHarness
{
    /// <summary>
    /// Mounts the full mobile admin users page as an ADMIN viewer.
    /// Returns the test context and the ordered list of every dispatched
    /// <see cref="GetUsersPaginatedQuery"/>.
    /// </summary>
    /// <param name="totalCount">The canned TotalCount for every users
    /// page (drives the count line + load-more visibility).</param>
    /// <param name="pageProvider">Answers each users query with the
    /// payload for ITS page number.</param>
    internal static (TestContext ctx, List<GetUsersPaginatedQuery> dispatched) Setup(
        int totalCount,
        Func<GetUsersPaginatedQuery, List<UserListItemDto>>? pageProvider = null)
    {
        var ctx = ComponentTestSetup.CreateRadzenEnabledContext();

        // Identity localizer with the format-args twin (count line).
        var loc = Substitute.For<IStringLocalizer<MobileAdminUsers>>();
        loc[Arg.Any<string>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        loc[Arg.Any<string>(), Arg.Any<object[]>()].Returns(ci =>
        {
            var args = ci.ArgAt<object[]>(1) ?? Array.Empty<object>();
            return new LocalizedString(
                (string)ci.ArgAt<string>(0),
                string.Join("|", args),
                false);
        });
        ctx.Services.AddSingleton(loc);

        // ToastService + ErrorDisplayService (page injects both).
        var errLoc = Substitute.For<IStringLocalizer<UnexpectedErrorMessages>>();
        errLoc[Arg.Any<string>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        ctx.Services.AddSingleton(errLoc);
        ctx.Services.AddSingleton(new ErrorDisplayService(errLoc));
        ctx.Services.AddSingleton(new Radzen.NotificationService());
        ctx.Services.AddSingleton(sp => new ToastService(
            sp.GetRequiredService<Radzen.NotificationService>(),
            sp.GetRequiredService<ErrorDisplayService>()));

        // The page injects ILogger + Radzen DialogService (the
        // activate/deactivate flows — not exercised here).
        ctx.Services.AddSingleton(Substitute.For<ILogger<MobileAdminUsers>>());
        ComponentTestSetup.AddDialogServiceSpy(ctx);

        // ADMIN viewer (the directory's full viewer).
        ComponentTestSetup.AddAuthenticatedUser(ctx, "admin", Roles.Admin);

        // The fake bus: canned answers through the per-page provider,
        // plus a RECORD of every users query, in order.
        var provider = pageProvider ?? (_ => MakeUsers(10));
        var dispatched = new List<GetUsersPaginatedQuery>();
        var bus = Substitute.For<IMessageBus>();

        bus.InvokeAsync<PaginatedResult<UserListItemDto>>(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var query = ci.ArgAt<object>(0).Should().BeOfType<GetUsersPaginatedQuery>().Subject;
                List<UserListItemDto> page;
                lock (dispatched)
                {
                    dispatched.Add(query);
                    page = provider(query);
                }
                return new PaginatedResult<UserListItemDto>(page, totalCount, query.PageNumber, query.PageSize);
            });

        ctx.Services.AddSingleton(bus);

        return (ctx, dispatched);
    }

    /// <summary>
    /// Makes <paramref name="count"/> distinct customer users (offset
    /// lets page 2+ payloads carry different names/ids than page 1).
    /// </summary>
    internal static List<UserListItemDto> MakeUsers(int count, int offset = 0) =>
        Enumerable.Range(1, count).Select(i =>
        {
            var n = offset + i;
            return new UserListItemDto
            {
                Id = Guid.NewGuid(),
                WorkerId = $"{10_000 + n}",
                FullName = $"User {n:000}",
                Gender = n % 2 == 0 ? Gender.Female : Gender.Male,
                GroupName = $"Group {n % 5}",
                IsActive = true,
                Roles = new List<string> { Roles.Customer }
            };
        }).ToList();
}

/// <summary>
/// Page-level bUnit tests for the QA-12 mobile admin user-directory
/// round: append-style "load more" pagination (the page previously
/// loaded ONE silent 100-row page — users past #100 were invisible on
/// mobile) and the dedicated filtered-to-zero message with its
/// clear-filters escape hatch.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHAT THIS PROVES</b>:
///   1. PAGINATION: the count line reflects the loaded/total split;
///      "Load more" appends the NEXT page as a follow-up query; the
///      button hides at the end.
///   2. NO-MATCHES COPY: a search matching zero rows over a NON-empty
///      directory shows the DEDICATED NoMatchesMessage (not the
///      true-empty "Create your first user account" copy), the
///      not-loaded-yet hint while pages remain unloaded, and a
///      Clear-filters button that restores the rows without a
///      round-trip.
/// </para>
/// <para>
/// <b>WIRING</b>: see <see cref="MobileAdminUsersPageTestHarness"/>.
/// Assertion style: sequence-based (the recording fake bus) — re-Find
/// elements before each event dispatch (Blazor re-renders after every
/// dispatch; captured references go stale).
/// </para>
/// </remarks>
public class MobileAdminUsersLoadMoreTests
{
    // The mobile page's loaded-window page size (mirrors the page const).
    private const int UsersPageSize = 100;

    private static (TestContext ctx, List<GetUsersPaginatedQuery> dispatched) Setup(
        int totalCount,
        Func<GetUsersPaginatedQuery, List<UserListItemDto>>? pageProvider = null)
        => MobileAdminUsersPageTestHarness.Setup(totalCount, pageProvider);

    /// <summary> Renders and waits for the initial list rows. </summary>
    private static IRenderedComponent<MobileAdminUsers> Mount(TestContext ctx, int expectedRows)
    {
        var cut = ctx.RenderComponent<MobileAdminUsers>();
        cut.WaitForAssertion(
            () => cut.FindAll(".m-list-item").Should().HaveCount(expectedRows),
            TimeSpan.FromSeconds(10));
        return cut;
    }

    [Fact]
    public async Task MobileAdminUsers_LoadMore_AppendsNextPageAndHidesAtEnd()
    {
        // Arrange — a 250-user directory, 100 per page; the provider
        // hands out DISTINCT payloads per page so appension is
        // observable in the rendered row count.
        const int total = 250;
        var (ctx, dispatched) = Setup(
            totalCount: total,
            pageProvider: q =>
            {
                var already = (q.PageNumber - 1) * UsersPageSize;
                return MobileAdminUsersPageTestHarness.MakeUsers(
                    Math.Min(UsersPageSize, total - already), offset: already);
            });
        using (ctx)
        {
            var cut = Mount(ctx, UsersPageSize);

            // The initial mount asked for page 1 of 100.
            dispatched.Should().HaveCountGreaterThanOrEqualTo(1);
            dispatched[0].PageNumber.Should().Be(1);
            dispatched[0].PageSize.Should().Be(UsersPageSize);

            // Count line reflects the loaded/total split.
            cut.Find(".m-list-count").TextContent.Trim().Should().Be($"1|{UsersPageSize}|{total}");

            // Act — Load more: appends page 2.
            await cut.InvokeAsync(() => cut.Find("button.m-load-more-btn").Click());

            // Assert — appended rows + advanced count line + follow-up
            // query pinned to page 2.
            cut.WaitForAssertion(
                () => cut.FindAll(".m-list-item").Should().HaveCount(UsersPageSize * 2),
                TimeSpan.FromSeconds(5));
            dispatched.Last().PageNumber.Should().Be(2);
            dispatched.Last().PageSize.Should().Be(UsersPageSize);
            cut.Find(".m-list-count").TextContent.Trim().Should().Be($"1|{UsersPageSize * 2}|{total}");

            // Act again — the final page (50 rows) closes the window.
            await cut.InvokeAsync(() => cut.Find("button.m-load-more-btn").Click());

            cut.WaitForAssertion(
                () => cut.FindAll(".m-list-item").Should().HaveCount(total),
                TimeSpan.FromSeconds(5));
            dispatched.Last().PageNumber.Should().Be(3);
            cut.Find(".m-list-count").TextContent.Trim().Should().Be($"1|{total}|{total}");

            // The button hides once everyone is loaded — the directory
            // is complete, not silently truncated at 100 anymore.
            cut.WaitForAssertion(
                () => cut.FindAll("button.m-load-more-btn").Should().BeEmpty(),
                TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task MobileAdminUsers_NoMatches_SearchShowsDedicatedMessageAndClearRestores()
    {
        // Arrange — a 250-user directory with only page 1 loaded.
        const int total = 250;
        var (ctx, _) = Setup(
            totalCount: total,
            pageProvider: _ => MobileAdminUsersPageTestHarness.MakeUsers(UsersPageSize));
        using (ctx)
        {
            var cut = Mount(ctx, UsersPageSize);

            // Act — search for something that matches nothing.
            await cut.InvokeAsync(() => cut.Find("input[type=search]").Input("zzz-no-match"));

            // Assert — the DEDICATED no-matches message (NOT the
            // true-empty "Create your first user account" copy), the
            // not-loaded-yet hint (window 100 < 250), and the
            // clear-filters escape hatch.
            cut.WaitForAssertion(
                () => cut.Find(".m-empty").TextContent.Should().Contain("NoMatchesMessage"),
                TimeSpan.FromSeconds(5));
            cut.Find(".m-empty").TextContent.Should().Contain("NoMatchesMoreHint");
            cut.Find(".m-empty .m-clear-filters-btn").Should().NotBeNull();

            // Act — clear the filters.
            await cut.InvokeAsync(() => cut.Find(".m-empty .m-clear-filters-btn").Click());

            // Assert — the rows return with no round-trip.
            cut.WaitForAssertion(
                () => cut.FindAll(".m-list-item").Should().HaveCount(UsersPageSize),
                TimeSpan.FromSeconds(5));
            cut.FindAll(".m-empty").Should().BeEmpty();
        }
    }
}
