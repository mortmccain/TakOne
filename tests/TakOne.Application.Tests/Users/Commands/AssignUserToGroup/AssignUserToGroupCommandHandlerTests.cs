using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Users.Commands.AssignUserToGroup;
using TakOne.Domain.Customers.Entities;
using TakOne.Domain.Users;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Users.Commands.AssignUserToGroup;

/// <summary>
/// Regression tests for <see cref="AssignUserToGroupCommandHandler"/>,
/// covering the Round 2 phantom-group guard plus the caller-scope
/// enforcement paths.
/// </summary>
/// <remarks>
/// <para>
/// <b>PHANTOM-GROUP GUARD (Round 2 deep-dive fix).</b> The command's
/// GroupId originates from an active-group dropdown, but the page can be
/// stale: the group may no longer exist or may have been deactivated in
/// another session. The handler now validates the group up front (single
/// indexed read) and returns a friendly failure instead of trusting the
/// DB FK constraint — which previously surfaced as a raw
/// DbUpdateException inside Wolverine's transaction middleware (an
/// "unexpected error" toast plus pointless retries of a doomed command).
/// </para>
/// <para>
/// <b>SCOPE ENFORCEMENT (Phase 6.5).</b> Admin may assign anyone's group;
/// Manager may assign Employee/Customer targets only; Employee may assign
/// Customer targets only; callers holding none of the three staff roles
/// are rejected outright (Round 1 fall-through guard).
/// </para>
/// <para>
/// The User and CustomerGroup aggregates are REAL (not mocked); all
/// collaborators are NSubstitute mocks.
/// </para>
/// </remarks>
public class AssignUserToGroupCommandHandlerTests
{
    private const string TargetWorkerId = "CUST-001";

    private static readonly Guid ActiveGroupId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid PhantomGroupId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    // ── Helpers ───────────────────────────────────────────────────────

    private static CustomerGroup BuildActiveGroup()
        => CustomerGroup.Create("VIP Customers", new Money(10_000m, "IRR"));

    private static CustomerGroup BuildInactiveGroup()
    {
        var group = CustomerGroup.Create("Legacy Tier", new Money(5_000m, "IRR"));
        group.Deactivate();
        return group;
    }

    private static User BuildTargetCustomer()
        => User.CreateCustomer(TargetWorkerId, "Alice", ActiveGroupId, Gender.Female);

    /// <summary>
    /// Builds the mock set. Defaults: authenticated ADMIN caller; target
    /// user (shared instance, so tests can assert on its post-mutation
    /// state) resolves with the Customer role; the group lookup returns
    /// an ACTIVE group. Each test overrides the piece it exercises.
    /// </summary>
    private static (
        ICurrentUserService currentUser,
        IUserRepository userRepo,
        ICustomerGroupRepository groupRepo,
        IUnitOfWork unitOfWork,
        ILogger<AssignUserToGroupCommandHandler> logger,
        User target)
        BuildMocks(User? target = null, CustomerGroup? group = null)
    {
        var targetUser = target ?? BuildTargetCustomer();

        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);
        currentUser.IsInRole(Roles.Admin).Returns(true);
        currentUser.IsInRole(Roles.Manager).Returns(false);
        currentUser.IsInRole(Roles.Employee).Returns(false);

        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(targetUser);
        userRepo.GetRolesByUserIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, List<string>>
            {
                [targetUser.Id] = [Roles.Customer]
            });

        var groupRepo = Substitute.For<ICustomerGroupRepository>();
        groupRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(group ?? BuildActiveGroup());

        var unitOfWork = Substitute.For<IUnitOfWork>();

        var logger = Substitute.For<ILogger<AssignUserToGroupCommandHandler>>();

        return (currentUser, userRepo, groupRepo, unitOfWork, logger, targetUser);
    }

    private static Task<Result> Invoke(
        ICurrentUserService currentUser,
        IUserRepository userRepo,
        ICustomerGroupRepository groupRepo,
        IUnitOfWork unitOfWork,
        ILogger<AssignUserToGroupCommandHandler> logger,
        Guid userId,
        Guid groupId)
        => AssignUserToGroupCommandHandler.HandleAsync(
            new AssignUserToGroupCommand(userId, groupId),
            currentUser,
            userRepo,
            groupRepo,
            unitOfWork,
            logger,
            CancellationToken.None);

    // ── Phantom-group guard (Round 2) ────────────────────────────────

    [Fact]
    public async Task HandleAsync_WithPhantomGroupId_ReturnsFriendlyNotFoundFailure()
    {
        // Arrange — group lookup returns null (group deleted / never existed).
        var (currentUser, userRepo, groupRepo, unitOfWork, logger, user) =
            BuildMocks(group: null);
        groupRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((CustomerGroup?)null);

        // Act
        var result = await Invoke(currentUser, userRepo, groupRepo, unitOfWork, logger,
            user.Id, PhantomGroupId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("was not found");
        result.Error.Should().Contain(PhantomGroupId.ToString());
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithDeactivatedGroup_ReturnsFriendlyInactiveFailure()
    {
        // Arrange — the group exists but is deactivated.
        var inactive = BuildInactiveGroup();
        var (currentUser, userRepo, groupRepo, unitOfWork, logger, user) =
            BuildMocks(group: inactive);

        // Act
        var result = await Invoke(currentUser, userRepo, groupRepo, unitOfWork, logger,
            user.Id, inactive.Id);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("deactivated");
        result.Error.Should().Contain(inactive.Name);
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithActiveGroup_AssignsAndSaves()
    {
        // Arrange
        var group = BuildActiveGroup();
        var (currentUser, userRepo, groupRepo, unitOfWork, logger, user) =
            BuildMocks(group: group);

        // Act
        var result = await Invoke(currentUser, userRepo, groupRepo, unitOfWork, logger,
            user.Id, group.Id);

        // Assert
        result.IsSuccess.Should().BeTrue();
        user.GroupId.Should().Be(group.Id);
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Pre-existing guard rails (regression coverage) ────────────────

    [Fact]
    public async Task HandleAsync_WhenUnauthenticated_ReturnsAuthenticationFailure()
    {
        // Arrange
        var (currentUser, userRepo, groupRepo, unitOfWork, logger, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        var result = await Invoke(currentUser, userRepo, groupRepo, unitOfWork, logger,
            Guid.NewGuid(), ActiveGroupId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Authentication required");
    }

    [Fact]
    public async Task HandleAsync_WhenTargetUserMissing_ReturnsUserNotFoundFailure()
    {
        // Arrange
        var (currentUser, userRepo, groupRepo, unitOfWork, logger, _) = BuildMocks();
        userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((User?)null);
        var missingUserId = Guid.NewGuid();

        // Act
        var result = await Invoke(currentUser, userRepo, groupRepo, unitOfWork, logger,
            missingUserId, ActiveGroupId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("was not found");
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NonAdminChangingOwnGroup_ReturnsFailure()
    {
        // Arrange — a Manager attempting to change their own group. The
        // self-check compares command.UserId against currentUser.UserId,
        // so the command targets the CALLER's Id.
        var (currentUser, userRepo, groupRepo, unitOfWork, logger, _) = BuildMocks();
        currentUser.IsInRole(Roles.Admin).Returns(false);
        currentUser.IsInRole(Roles.Manager).Returns(true);

        // Act — target the caller themselves.
        var result = await Invoke(currentUser, userRepo, groupRepo, unitOfWork, logger,
            TestValues.CreatedByUserId, ActiveGroupId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("own group");
    }

    [Fact]
    public async Task HandleAsync_ManagerAssigningManager_ReturnsFailure()
    {
        // Arrange — target holds Manager (off-limits to a Manager caller).
        var (currentUser, userRepo, groupRepo, unitOfWork, logger, anotherManager) =
            BuildMocks();
        currentUser.IsInRole(Roles.Admin).Returns(false);
        currentUser.IsInRole(Roles.Manager).Returns(true);
        userRepo.GetRolesByUserIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, List<string>>
            {
                [anotherManager.Id] = [Roles.Manager]
            });

        // Act
        var result = await Invoke(currentUser, userRepo, groupRepo, unitOfWork, logger,
            anotherManager.Id, ActiveGroupId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Managers may only change the group");
    }

    [Fact]
    public async Task HandleAsync_ManagerAssigningCustomer_Succeeds()
    {
        // Arrange — target's roles default to [Customer] in BuildMocks.
        var (currentUser, userRepo, groupRepo, unitOfWork, logger, customer) = BuildMocks();
        currentUser.IsInRole(Roles.Admin).Returns(false);
        currentUser.IsInRole(Roles.Manager).Returns(true);

        // Act
        var result = await Invoke(currentUser, userRepo, groupRepo, unitOfWork, logger,
            customer.Id, ActiveGroupId);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_EmployeeAssigningEmployee_ReturnsFailure()
    {
        // Arrange — target holds Employee (off-limits to an Employee caller).
        var (currentUser, userRepo, groupRepo, unitOfWork, logger, anotherEmployee) =
            BuildMocks();
        currentUser.IsInRole(Roles.Admin).Returns(false);
        currentUser.IsInRole(Roles.Manager).Returns(false);
        currentUser.IsInRole(Roles.Employee).Returns(true);
        userRepo.GetRolesByUserIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, List<string>>
            {
                [anotherEmployee.Id] = [Roles.Employee]
            });

        // Act
        var result = await Invoke(currentUser, userRepo, groupRepo, unitOfWork, logger,
            anotherEmployee.Id, ActiveGroupId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Employees may only change the group");
    }

    [Fact]
    public async Task HandleAsync_CallerHoldingNoStaffRole_ReturnsFailure()
    {
        // Arrange — the Round 1 fall-through guard: caller holds none of
        // Admin/Manager/Employee (e.g. ReadOnly or Customer).
        var (currentUser, userRepo, groupRepo, unitOfWork, logger, customer) = BuildMocks();
        currentUser.IsInRole(Roles.Admin).Returns(false);
        currentUser.IsInRole(Roles.Manager).Returns(false);
        currentUser.IsInRole(Roles.Employee).Returns(false);

        // Act
        var result = await Invoke(currentUser, userRepo, groupRepo, unitOfWork, logger,
            customer.Id, ActiveGroupId);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Only administrators, managers, and employees");
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
