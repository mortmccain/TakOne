using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Users.Commands.CreateCustomer;
using TakOne.Domain.Customers.Entities;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Users.Commands.CreateCustomer;

/// <summary>
/// Regression tests for <see cref="CreateCustomerCommandHandler"/>,
/// covering the Round 2 phantom-group guard plus the pre-existing
/// failure modes (duplicate WorkerId, Identity-account failure with the
/// critical change-tracker unwind).
/// </summary>
/// <remarks>
/// <para>
/// <b>PHANTOM-GROUP GUARD (Round 2 deep-dive fix).</b> A stale CreateUser
/// page (or a hand-crafted command) can submit a GroupId for a group that
/// no longer exists or has been deactivated. Without the guard, the
/// failure surfaces as an FK-violation DbUpdateException at the FINAL
/// SaveChanges — after the Identity account was already created — leaving
/// Wolverine's transaction middleware to unwind it while the admin sees
/// an opaque "unexpected error" toast. The guard validates the group
/// BEFORE any mutation.
/// </para>
/// <para>
/// The CustomerGroup aggregate is REAL; all collaborators are NSubstitute
/// mocks. The Domain User is built by the handler itself (real factory).
/// </para>
/// </remarks>
public class CreateCustomerCommandHandlerTests
{
    private const string NewWorkerId = "CUST-NEW-001";
    private const string NewEmail = "new.customer@example.com";
    private const string NewPassword = "P@ssw0rd!";

    private static readonly Guid ActiveGroupId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid PhantomGroupId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static CreateCustomerCommand BuildCommand(Guid groupId)
        => new(NewWorkerId, "Alice", groupId, NewEmail, NewPassword, Domain.Users.Gender.Female);

    /// <summary>
    /// Builds the mock set. Defaults: authenticated caller; WorkerId is
    /// free; the group lookup returns an ACTIVE group; the Identity
    /// account service succeeds; SaveChanges returns 1.
    /// </summary>
    private static (
        ICurrentUserService currentUser,
        IUserRepository userRepo,
        ICustomerGroupRepository groupRepo,
        IUserAccountService accountService,
        IUnitOfWork unitOfWork,
        ILogger<CreateCustomerCommandHandler> logger)
        BuildMocks(CustomerGroup? group = null, bool accountSucceeds = true)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);

        var userRepo = Substitute.For<IUserRepository>();
        userRepo.WorkerIdExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var groupRepo = Substitute.For<ICustomerGroupRepository>();
        groupRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(group ?? CustomerGroup.Create("VIP Customers", new Money(10_000m, "IRR")));

        var accountService = Substitute.For<IUserAccountService>();
        accountService.CreateIdentityAccountAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Domain.Users.Gender>(),
                Arg.Any<CancellationToken>())
            .Returns(accountSucceeds
                ? Result.Success()
                : Result.Failure("Password too weak."));

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(1);

        var logger = Substitute.For<ILogger<CreateCustomerCommandHandler>>();

        return (currentUser, userRepo, groupRepo, accountService, unitOfWork, logger);
    }

    private static Task<Result<Guid>> Invoke(
        ICurrentUserService currentUser,
        IUserRepository userRepo,
        ICustomerGroupRepository groupRepo,
        IUserAccountService accountService,
        IUnitOfWork unitOfWork,
        ILogger<CreateCustomerCommandHandler> logger,
        CreateCustomerCommand command)
        => CreateCustomerCommandHandler.HandleAsync(
            command,
            currentUser,
            userRepo,
            groupRepo,
            accountService,
            unitOfWork,
            logger,
            CancellationToken.None);

    // ── Phantom-group guard (Round 2) ────────────────────────────────

    [Fact]
    public async Task HandleAsync_WithPhantomGroupId_ReturnsFriendlyNotFoundFailure_BeforeAnyMutation()
    {
        // Arrange — the group referenced by the command does not exist.
        var (currentUser, userRepo, groupRepo, accountService, unitOfWork, logger) =
            BuildMocks(group: null);
        groupRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((CustomerGroup?)null);

        // Act
        var result = await Invoke(currentUser, userRepo, groupRepo, accountService,
            unitOfWork, logger, BuildCommand(PhantomGroupId));

        // Assert — nothing was mutated: no user added, no Identity
        // account created, no SaveChanges, no tracker unwind needed.
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("was not found");
        result.Error.Should().Contain(PhantomGroupId.ToString());
        await userRepo.DidNotReceive().AddAsync(
            Arg.Any<Domain.Users.User>(), Arg.Any<CancellationToken>());
        await accountService.DidNotReceiveWithAnyArgs().CreateIdentityAccountAsync(
            default(Guid), string.Empty, string.Empty, string.Empty, string.Empty,
            default(Domain.Users.Gender), default(CancellationToken));
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithDeactivatedGroup_ReturnsFriendlyInactiveFailure_BeforeAnyMutation()
    {
        // Arrange — the group exists but is deactivated.
        var inactive = CustomerGroup.Create("Legacy Tier", new Money(5_000m, "IRR"));
        inactive.Deactivate();
        var (currentUser, userRepo, groupRepo, accountService, unitOfWork, logger) =
            BuildMocks(group: inactive);

        // Act
        var result = await Invoke(currentUser, userRepo, groupRepo, accountService,
            unitOfWork, logger, BuildCommand(inactive.Id));

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("deactivated");
        result.Error.Should().Contain(inactive.Name);
        await userRepo.DidNotReceive().AddAsync(
            Arg.Any<Domain.Users.User>(), Arg.Any<CancellationToken>());
        await accountService.DidNotReceiveWithAnyArgs().CreateIdentityAccountAsync(
            default(Guid), string.Empty, string.Empty, string.Empty, string.Empty,
            default(Domain.Users.Gender), default(CancellationToken));
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Pre-existing failure modes (regression coverage) ──────────────

    [Fact]
    public async Task HandleAsync_WhenWorkerIdAlreadyExists_ReturnsDuplicateFailure()
    {
        // Arrange
        var (currentUser, userRepo, groupRepo, accountService, unitOfWork, logger) = BuildMocks();
        userRepo.WorkerIdExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await Invoke(currentUser, userRepo, groupRepo, accountService,
            unitOfWork, logger, BuildCommand(ActiveGroupId));

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already exists");
        await accountService.DidNotReceiveWithAnyArgs().CreateIdentityAccountAsync(
            default(Guid), string.Empty, string.Empty, string.Empty, string.Empty,
            default(Domain.Users.Gender), default(CancellationToken));
    }

    [Fact]
    public async Task HandleAsync_WhenUnauthenticated_ReturnsAuthenticationFailure()
    {
        // Arrange
        var (currentUser, userRepo, groupRepo, accountService, unitOfWork, logger) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        var result = await Invoke(currentUser, userRepo, groupRepo, accountService,
            unitOfWork, logger, BuildCommand(ActiveGroupId));

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Authentication required");
    }

    [Fact]
    public async Task HandleAsync_WhenIdentityAccountFails_DetachesDomainUserAndReturnsFailure()
    {
        // Arrange — v6.2 regression: the tracked Domain User must be
        // detached (ClearChangeTracker) so Wolverine's transaction
        // middleware doesn't persist an orphaned Domain User.
        var (currentUser, userRepo, groupRepo, accountService, unitOfWork, logger) =
            BuildMocks(accountSucceeds: false);

        // Act
        var result = await Invoke(currentUser, userRepo, groupRepo, accountService,
            unitOfWork, logger, BuildCommand(ActiveGroupId));

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Password too weak");
        unitOfWork.Received(1).ClearChangeTracker();
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Success path ──────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WithActiveGroup_CreatesAccountAndReturnsUserId()
    {
        // Arrange
        var (currentUser, userRepo, groupRepo, accountService, unitOfWork, logger) = BuildMocks();

        // Act
        var result = await Invoke(currentUser, userRepo, groupRepo, accountService,
            unitOfWork, logger, BuildCommand(ActiveGroupId));

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBe(Guid.Empty);
        await userRepo.Received(1).AddAsync(
            Arg.Any<Domain.Users.User>(), Arg.Any<CancellationToken>());
        await accountService.Received(1).CreateIdentityAccountAsync(
            Arg.Any<Guid>(),
            NewWorkerId,
            NewEmail,
            NewPassword,
            Arg.Is<string>(r => r == Roles.Customer),
            Arg.Any<Domain.Users.Gender>(),
            Arg.Any<CancellationToken>());
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
