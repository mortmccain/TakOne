using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Users.Queries.GetUsersPaginated;
using TakOne.Domain.Users;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Users.Queries.GetUsersPaginated;

/// <summary>
/// Unit tests for <see cref="GetUsersPaginatedQueryHandler"/> — the FIRST
/// tests for this handler (Round 5). They pin:
///   1. The defense-in-depth auth model (empty page for unauthenticated /
///      non-staff callers — no repository call at all).
///   2. The Round-5 filter/sort wiring: every query field flows into the
///      <see cref="UsersListFilters"/> record handed to the repository.
///   3. The GroupName visibility rule (stripped for Employee callers,
///      resolved for Admin/Manager) and the group-load failure degradation.
///   4. Page-parameter clamping (the MaxPageSize=100 clamp that the
///      pre-Round-5 page silently tripped over).
///   5. The roles batch load onto each DTO.
/// </summary>
public class GetUsersPaginatedQueryHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static (
        ICurrentUserService currentUser,
        IUserRepository userRepo,
        ICustomerGroupRepository groupRepo,
        ILogger<GetUsersPaginatedQueryHandler> logger)
        BuildMocks(string role, Guid? callerId = null)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(callerId ?? TestValues.CustomerId);
        currentUser.IsInRole(Arg.Any<string>()).Returns(false);
        currentUser.IsInRole(role).Returns(true);

        var userRepo = Substitute.For<IUserRepository>();
        // Default: empty page — individual tests override via When(...).
        userRepo.GetPaginatedAsync(
                Arg.Any<UsersListFilters?>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => new PaginatedResult<User>(
                new List<User>(),
                0,
                ci.ArgAt<int>(1),
                ci.ArgAt<int>(2)));
        userRepo.GetRolesByUserIdsAsync(
                Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, List<string>>());

        var groupRepo = Substitute.For<ICustomerGroupRepository>();
        groupRepo.GetAllAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Domain.Customers.Entities.CustomerGroup>());

        var logger = Substitute.For<ILogger<GetUsersPaginatedQueryHandler>>();

        return (currentUser, userRepo, groupRepo, logger);
    }

    private static UsersListFilters CapturedFilters(IUserRepository userRepo)
    {
        var filters = userRepo.ReceivedCalls()
            .Select(c => c.GetArguments().FirstOrDefault(a => a is UsersListFilters))
            .Cast<UsersListFilters>()
            .FirstOrDefault();
        filters.Should().NotBeNull("the handler must hand filters to the repository");
        return filters!;
    }

    private static User MakeUser(
        string workerId = "EMP-1",
        string fullName = "Test User",
        Guid? groupId = null,
        Gender gender = Gender.Male,
        bool active = true)
    {
        var user = groupId is null
            ? User.CreateStaff(workerId, fullName, gender)
            : User.CreateCustomer(workerId, fullName, groupId.Value, gender);
        if (!active)
        {
            user.Deactivate();
        }
        return user;
    }

    private static Task<PaginatedResult<TakOne.Application.Users.DTOs.UserListItemDto>> HandleAsync(
        GetUsersPaginatedQuery query,
        ICurrentUserService currentUser,
        IUserRepository userRepo,
        ICustomerGroupRepository groupRepo,
        ILogger<GetUsersPaginatedQueryHandler> logger)
        => GetUsersPaginatedQueryHandler.HandleAsync(
            query, currentUser, userRepo, groupRepo, logger, CancellationToken.None);

    // ── Defense-in-depth auth ───────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_Unauthenticated_ReturnsEmptyPageWithoutRepositoryCall()
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(false);
        currentUser.UserId.Returns(Guid.Empty);
        var ( _, userRepo, groupRepo, logger) = BuildMocks(Roles.Admin);

        var result = await HandleAsync(
            new GetUsersPaginatedQuery(), currentUser, userRepo, groupRepo, logger);

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
        await userRepo.DidNotReceive().GetPaginatedAsync(
            Arg.Any<UsersListFilters?>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_CustomerRole_ReturnsEmptyPageWithoutRepositoryCall()
    {
        // Customers may not list users — the auth middleware rejects the
        // call; the handler double-checks (defense-in-depth).
        var (currentUser, userRepo, groupRepo, logger) = BuildMocks(Roles.Customer);

        var result = await HandleAsync(
            new GetUsersPaginatedQuery(), currentUser, userRepo, groupRepo, logger);

        result.Items.Should().BeEmpty();
        await userRepo.DidNotReceive().GetPaginatedAsync(
            Arg.Any<UsersListFilters?>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Page-parameter clamping ─────────────────────────────────────────

    [Theory]
    [InlineData(0, 20, 1, 20)]    // page 0 → 1; size 0 → default 20
    [InlineData(-5, -1, 1, 20)]   // negatives → defaults
    [InlineData(3, 500, 3, 100)]  // the pre-Round-5 AdminUsers shape: 500 → the 100 cap
    [InlineData(2, 50, 2, 50)]    // sane values pass through
    public async Task HandleAsync_ClampsPageParameters(
        int requestPage, int requestSize, int expectedPage, int expectedSize)
    {
        var (currentUser, userRepo, groupRepo, logger) = BuildMocks(Roles.Admin);

        await HandleAsync(
            new GetUsersPaginatedQuery { PageNumber = requestPage, PageSize = requestSize },
            currentUser, userRepo, groupRepo, logger);

        await userRepo.Received(1).GetPaginatedAsync(
            Arg.Any<UsersListFilters?>(),
            expectedPage, expectedSize, Arg.Any<CancellationToken>());
    }

    // ── Round-5 filter/sort wiring ──────────────────────────────────────

    [Fact]
    public async Task HandleAsync_DefaultQuery_FiltersAreAllClear()
    {
        var (currentUser, userRepo, groupRepo, logger) = BuildMocks(Roles.Admin);

        await HandleAsync(new GetUsersPaginatedQuery(), currentUser, userRepo, groupRepo, logger);

        var filters = CapturedFilters(userRepo);
        filters.SearchTerm.Should().BeNull();
        filters.GroupId.Should().BeNull();
        filters.IsActive.Should().BeNull();
        filters.Gender.Should().BeNull();
        filters.WorkerId.Should().BeNull();
        filters.FullName.Should().BeNull();
        filters.SortBy.Should().BeNull("no user sort → repo defaults to FullName ascending");
        filters.SortDescending.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_AllFilters_PassThroughToRepository()
    {
        var (currentUser, userRepo, groupRepo, logger) = BuildMocks(Roles.Admin);
        var groupId = Guid.NewGuid();
        var workerIdFilter = new UsersTextFilter("EMP-", UsersTextOperator.StartsWith);
        var fullNameFilter = new UsersTextFilter("smith", UsersTextOperator.NotEquals);

        await HandleAsync(new GetUsersPaginatedQuery
        {
            SearchTerm = "  jane  ",
            GroupId = groupId,
            IsActive = false,
            Gender = Gender.Female,
            WorkerIdFilter = workerIdFilter,
            FullNameFilter = fullNameFilter,
            SortBy = UsersSortBy.WorkerId,
            SortDescending = true
        }, currentUser, userRepo, groupRepo, logger);

        var filters = CapturedFilters(userRepo);
        filters.SearchTerm.Should().Be("  jane  ", "trimming is the repository's job; the handler passes through");
        filters.GroupId.Should().Be(groupId);
        filters.IsActive.Should().BeFalse();
        filters.Gender.Should().Be(Gender.Female);
        filters.WorkerId.Should().Be(workerIdFilter);
        filters.FullName.Should().Be(fullNameFilter);
        filters.SortBy.Should().Be(UsersSortBy.WorkerId);
        filters.SortDescending.Should().BeTrue();
    }

    // ── GroupName visibility + enrichment ───────────────────────────────

    [Fact]
    public async Task HandleAsync_AdminCaller_GroupNameResolvedFromGroupLookup()
    {
        var (currentUser, userRepo, groupRepo, logger) = BuildMocks(Roles.Admin);
        var group = Domain.Customers.Entities.CustomerGroup.Create(
            "VIP", new SharedKernel.ValueObjects.Money(1000m, "IRR"));
        var user = MakeUser(groupId: group.Id);

        userRepo.GetPaginatedAsync(
                Arg.Any<UsersListFilters?>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<User>(new List<User> { user }, 1, 1, 20));
        groupRepo.GetAllAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Domain.Customers.Entities.CustomerGroup> { group });

        var result = await HandleAsync(
            new GetUsersPaginatedQuery(), currentUser, userRepo, groupRepo, logger);

        result.Items.Should().ContainSingle().Which
            .GroupName.Should().Be("VIP");
    }

    [Fact]
    public async Task HandleAsync_EmployeeCaller_GroupNameStripped()
    {
        var (currentUser, userRepo, groupRepo, logger) = BuildMocks(Roles.Employee);
        var group = Domain.Customers.Entities.CustomerGroup.Create(
            "VIP", new SharedKernel.ValueObjects.Money(1000m, "IRR"));
        var user = MakeUser(groupId: group.Id);

        userRepo.GetPaginatedAsync(
                Arg.Any<UsersListFilters?>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<User>(new List<User> { user }, 1, 1, 20));
        groupRepo.GetAllAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Domain.Customers.Entities.CustomerGroup> { group });

        var result = await HandleAsync(
            new GetUsersPaginatedQuery(), currentUser, userRepo, groupRepo, logger);

        result.Items.Should().ContainSingle().Which
            .GroupName.Should().BeNull("GroupName is internal data — Employee callers see it stripped");
    }

    [Fact]
    public async Task HandleAsync_GroupLoadFails_UsersStillReturnedWithNullGroupName()
    {
        var (currentUser, userRepo, groupRepo, logger) = BuildMocks(Roles.Admin);
        var user = MakeUser(groupId: Guid.NewGuid());

        userRepo.GetPaginatedAsync(
                Arg.Any<UsersListFilters?>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<User>(new List<User> { user }, 1, 1, 20));
        groupRepo.GetAllAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("db down"));

        var result = await HandleAsync(
            new GetUsersPaginatedQuery(), currentUser, userRepo, groupRepo, logger);

        result.Items.Should().ContainSingle().Which.GroupName.Should().BeNull(
            "the group enrichment is non-fatal — the users come back without names");
    }

    // ── Roles batch + DTO projection ────────────────────────────────────

    [Fact]
    public async Task HandleAsync_RolesBatchLoadedOntoDtos()
    {
        var (currentUser, userRepo, groupRepo, logger) = BuildMocks(Roles.Admin);
        var user1 = MakeUser(workerId: "EMP-1");
        var user2 = MakeUser(workerId: "EMP-2");

        userRepo.GetPaginatedAsync(
                Arg.Any<UsersListFilters?>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<User>(new List<User> { user1, user2 }, 2, 1, 20));
        userRepo.GetRolesByUserIdsAsync(
                Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, List<string>>
            {
                [user1.Id] = new List<string> { Roles.Admin, Roles.Manager }
            });

        var result = await HandleAsync(
            new GetUsersPaginatedQuery(), currentUser, userRepo, groupRepo, logger);

        result.Items.Should().HaveCount(2);
        result.Items.Single(u => u.Id == user1.Id).Roles
            .Should().BeEquivalentTo(new[] { Roles.Admin, Roles.Manager });
        result.Items.Single(u => u.Id == user2.Id).Roles
            .Should().BeEmpty("a missing key means no roles — not an error");
    }

    [Fact]
    public async Task HandleAsync_TotalCountFlowsFromRepository()
    {
        var (currentUser, userRepo, groupRepo, logger) = BuildMocks(Roles.Admin);
        var user = MakeUser();

        userRepo.GetPaginatedAsync(
                Arg.Any<UsersListFilters?>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<User>(new List<User> { user }, 451, 1, 20));

        var result = await HandleAsync(
            new GetUsersPaginatedQuery { PageNumber = 1, PageSize = 20 },
            currentUser, userRepo, groupRepo, logger);

        result.TotalCount.Should().Be(451,
            "the server-side total drives the grid pager — with the pre-Round-5 clamp " +
            "the page believed there were at most 100 users");
        result.PageNumber.Should().Be(1);
        result.PageSize.Should().Be(20);
    }
}
