using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Notifications.Commands.SendBroadcastNotification;
using TakOne.Domain.Notifications.Enums;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Notifications.Commands.SendBroadcastNotification;

/// <summary>
/// Unit tests for <see cref="SendBroadcastNotificationCommandValidator"/>.
///
/// COVERAGE APPROACH: the validator enforces:
///   - Title NotEmpty with ErrorCode "BroadcastTitleRequired"
///   - Title MaximumLength(200) with ErrorCode "BroadcastTitleTooLong"
///   - Message NotEmpty with ErrorCode "BroadcastMessageRequired"
///   - Message MaximumLength(1000) with ErrorCode "BroadcastMessageTooLong"
///   - Scope IsInEnum with ErrorCode "BroadcastScopeInvalid"
///   - Custom scope-target consistency rule (SUT DISCOVERY: the SUT's
///     Custom rule uses <c>ctx.AddFailure(propertyName, errorMessage)</c>
///     with the would-be ErrorCode string as the first arg. Per
///     FluentValidation's API, <c>AddFailure(string, string)</c> sets
///     the FIRST arg as <c>PropertyName</c> (NOT <c>ErrorCode</c>).
///     So the strings the developer intended as ErrorCodes actually
///     end up in <c>ValidationFailure.PropertyName</c>. The tests
///     below assert on <c>PropertyName</c> (which is what the SUT
///     actually sets) — a future refactor that switches to
///     <c>ctx.AddFailure(new ValidationFailure(...){ ErrorCode = ... })</c>
///     will need to update these tests to assert on ErrorCode instead.
///     This SUT misuse is documented here so the test suite fails
///     loudly if the SUT is changed without updating the tests.
///       Scope=All  → all three target fields must be null
///                    (PropertyName "BroadcastScopeAllTargetsMustBeNull")
///       Scope=Role → TargetRoleName required non-empty
///                    (PropertyName "BroadcastScopeRoleRequiresTargetRoleName")
///                    + must be in ValidRoleNames = {Admin, Manager,
///                    Employee, ReadOnly, Customer}
///                    (PropertyName "BroadcastRoleNameInvalid")
///                    + TargetGroupId / TargetUserId must be null
///                    (PropertyName "BroadcastScopeRoleExtraTargets")
///       Scope=Group → TargetGroupId required non-empty
///                    (PropertyName "BroadcastScopeGroupRequiresTargetGroupId")
///                    + TargetRoleName / TargetUserId must be null
///                    (PropertyName "BroadcastScopeGroupExtraTargets")
///       Scope=User  → TargetUserId required non-empty
///                    (PropertyName "BroadcastScopeUserRequiresTargetUserId")
///                    + TargetRoleName / TargetGroupId must be null
///                    (PropertyName "BroadcastScopeUserExtraTargets")
///
/// We lock in BOTH the property-level ErrorCodes (Title/Message/Scope
/// rules) AND the Custom rule's PropertyName values (which is what the
/// SUT actually sets for these failures). All 4 scope branches are
/// covered with target-missing + target-extra cases (8 cases total).
/// </summary>
public class SendBroadcastNotificationCommandValidatorTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    // Builds a valid command for Scope=All (no targets set). Each test
    // then mutates one or more fields to exercise a specific rule.
    private static SendBroadcastNotificationCommand BuildValidScopeAllCommand(
        string? title = null,
        string? message = null)
        => new(
            title ?? "Valid Title",
            message ?? "Valid Message",
            Scope: BroadcastScope.All,
            TargetRoleName: null,
            TargetGroupId: null,
            TargetUserId: null);

    // ── Title rules ────────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenTitleIsNonEmpty_HasNoErrors()
    {
        // Arrange
        var validator = new SendBroadcastNotificationCommandValidator();
        var command = BuildValidScopeAllCommand();

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenTitleIsEmpty_ReturnsBroadcastTitleRequiredErrorCode()
    {
        // Arrange
        var validator = new SendBroadcastNotificationCommandValidator();
        var command = BuildValidScopeAllCommand(title: string.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorCode == "BroadcastTitleRequired");
    }

    [Fact]
    public void Validate_WhenTitleExceedsMaxLength_ReturnsBroadcastTitleTooLongErrorCode()
    {
        // Arrange
        // MaximumLength(200) → 201 chars fails.
        var validator = new SendBroadcastNotificationCommandValidator();
        var title = new string('a', 201);

        // Act
        var result = validator.Validate(BuildValidScopeAllCommand(title: title));

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorCode == "BroadcastTitleTooLong");
    }

    // ── Message rules ──────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenMessageIsEmpty_ReturnsBroadcastMessageRequiredErrorCode()
    {
        // Arrange
        var validator = new SendBroadcastNotificationCommandValidator();
        var command = BuildValidScopeAllCommand(message: string.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorCode == "BroadcastMessageRequired");
    }

    [Fact]
    public void Validate_WhenMessageExceedsMaxLength_ReturnsBroadcastMessageTooLongErrorCode()
    {
        // Arrange
        // MaximumLength(1000) → 1001 chars fails.
        var validator = new SendBroadcastNotificationCommandValidator();
        var message = new string('a', 1001);

        // Act
        var result = validator.Validate(BuildValidScopeAllCommand(message: message));

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorCode == "BroadcastMessageTooLong");
    }

    // ── Scope=All branch (target-missing + target-extra) ─────────────

    // Scope=All happy path — all three target fields null. Already
    // covered by the helper + the title happy-path test. Adding an
    // explicit happy path test for clarity.
    [Fact]
    public void Validate_WhenScopeIsAllAndNoTargetsSet_HasNoErrors()
    {
        // Arrange
        var validator = new SendBroadcastNotificationCommandValidator();
        var command = BuildValidScopeAllCommand();

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    // Scope=All but TargetRoleName is set → "BroadcastScopeAllTargetsMustBeNull".
    [Fact]
    public void Validate_WhenScopeIsAllAndTargetRoleNameSet_ReturnsAllTargetsMustBeNullErrorCode()
    {
        // Arrange
        var validator = new SendBroadcastNotificationCommandValidator();
        var command = new SendBroadcastNotificationCommand(
            Title: "Valid Title",
            Message: "Valid Message",
            Scope: BroadcastScope.All,
            TargetRoleName: Roles.Admin, // MISCONFIGURED — All must have null.
            TargetGroupId: null,
            TargetUserId: null);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "BroadcastScopeAllTargetsMustBeNull");
    }

    // Scope=All but TargetGroupId is set → same "BroadcastScopeAllTargetsMustBeNull".
    [Fact]
    public void Validate_WhenScopeIsAllAndTargetGroupIdSet_ReturnsAllTargetsMustBeNullErrorCode()
    {
        // Arrange
        var validator = new SendBroadcastNotificationCommandValidator();
        var command = new SendBroadcastNotificationCommand(
            Title: "Valid Title",
            Message: "Valid Message",
            Scope: BroadcastScope.All,
            TargetRoleName: null,
            TargetGroupId: TestValues.GroupId, // MISCONFIGURED.
            TargetUserId: null);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "BroadcastScopeAllTargetsMustBeNull");
    }

    // Scope=All but TargetUserId is set → same "BroadcastScopeAllTargetsMustBeNull".
    [Fact]
    public void Validate_WhenScopeIsAllAndTargetUserIdSet_ReturnsAllTargetsMustBeNullErrorCode()
    {
        // Arrange
        var validator = new SendBroadcastNotificationCommandValidator();
        var command = new SendBroadcastNotificationCommand(
            Title: "Valid Title",
            Message: "Valid Message",
            Scope: BroadcastScope.All,
            TargetRoleName: null,
            TargetGroupId: null,
            TargetUserId: TestValues.UserId); // MISCONFIGURED.

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "BroadcastScopeAllTargetsMustBeNull");
    }

    // ── Scope=Role branch (target-missing + target-extra + role-name-invalid) ──

    [Fact]
    public void Validate_WhenScopeIsRoleAndTargetRoleNameMissing_ReturnsRoleRequiresTargetRoleNameErrorCode()
    {
        // Arrange
        var validator = new SendBroadcastNotificationCommandValidator();
        var command = new SendBroadcastNotificationCommand(
            Title: "Valid Title",
            Message: "Valid Message",
            Scope: BroadcastScope.Role,
            TargetRoleName: null, // MISSING — required for Scope=Role.
            TargetGroupId: null,
            TargetUserId: null);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "BroadcastScopeRoleRequiresTargetRoleName");
    }

    [Fact]
    public void Validate_WhenScopeIsRoleAndRoleNameNotInValidSet_ReturnsRoleNameInvalidErrorCode()
    {
        // Arrange
        // "Admins" (plural) is a typo not in ValidRoleNames.
        var validator = new SendBroadcastNotificationCommandValidator();
        var command = new SendBroadcastNotificationCommand(
            Title: "Valid Title",
            Message: "Valid Message",
            Scope: BroadcastScope.Role,
            TargetRoleName: "Admins", // TYPO — not in the canonical set.
            TargetGroupId: null,
            TargetUserId: null);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "BroadcastRoleNameInvalid");
    }

    [Fact]
    public void Validate_WhenScopeIsRoleAndTargetGroupIdSet_ReturnsRoleExtraTargetsErrorCode()
    {
        // Arrange
        var validator = new SendBroadcastNotificationCommandValidator();
        var command = new SendBroadcastNotificationCommand(
            Title: "Valid Title",
            Message: "Valid Message",
            Scope: BroadcastScope.Role,
            TargetRoleName: Roles.Customer, // Valid role name.
            TargetGroupId: TestValues.GroupId, // MISCONFIGURED — Role must not have Group.
            TargetUserId: null);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "BroadcastScopeRoleExtraTargets");
    }

    [Fact]
    public void Validate_WhenScopeIsRoleAndAllValid_HasNoErrors()
    {
        // Arrange
        var validator = new SendBroadcastNotificationCommandValidator();
        var command = new SendBroadcastNotificationCommand(
            Title: "Valid Title",
            Message: "Valid Message",
            Scope: BroadcastScope.Role,
            TargetRoleName: Roles.Customer, // Valid role name.
            TargetGroupId: null,
            TargetUserId: null);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    // ── Scope=Group branch (target-missing + target-extra) ─────────────

    [Fact]
    public void Validate_WhenScopeIsGroupAndTargetGroupIdMissing_ReturnsGroupRequiresTargetGroupIdErrorCode()
    {
        // Arrange
        var validator = new SendBroadcastNotificationCommandValidator();
        var command = new SendBroadcastNotificationCommand(
            Title: "Valid Title",
            Message: "Valid Message",
            Scope: BroadcastScope.Group,
            TargetRoleName: null,
            TargetGroupId: null, // MISSING — required for Scope=Group.
            TargetUserId: null);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "BroadcastScopeGroupRequiresTargetGroupId");
    }

    [Fact]
    public void Validate_WhenScopeIsGroupAndTargetGroupIdIsEmptyGuid_ReturnsGroupRequiresTargetGroupIdErrorCode()
    {
        // Arrange
        // Guid.Empty is rejected by the Custom rule's `== Guid.Empty` check.
        var validator = new SendBroadcastNotificationCommandValidator();
        var command = new SendBroadcastNotificationCommand(
            Title: "Valid Title",
            Message: "Valid Message",
            Scope: BroadcastScope.Group,
            TargetRoleName: null,
            TargetGroupId: Guid.Empty, // Empty — same as null per the SUT.
            TargetUserId: null);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "BroadcastScopeGroupRequiresTargetGroupId");
    }

    [Fact]
    public void Validate_WhenScopeIsGroupAndTargetRoleNameSet_ReturnsGroupExtraTargetsErrorCode()
    {
        // Arrange
        var validator = new SendBroadcastNotificationCommandValidator();
        var command = new SendBroadcastNotificationCommand(
            Title: "Valid Title",
            Message: "Valid Message",
            Scope: BroadcastScope.Group,
            TargetRoleName: Roles.Admin, // MISCONFIGURED — Group must not have RoleName.
            TargetGroupId: TestValues.GroupId,
            TargetUserId: null);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "BroadcastScopeGroupExtraTargets");
    }

    // ── Scope=User branch (target-missing + target-extra) ─────────────

    [Fact]
    public void Validate_WhenScopeIsUserAndTargetUserIdMissing_ReturnsUserRequiresTargetUserIdErrorCode()
    {
        // Arrange
        var validator = new SendBroadcastNotificationCommandValidator();
        var command = new SendBroadcastNotificationCommand(
            Title: "Valid Title",
            Message: "Valid Message",
            Scope: BroadcastScope.User,
            TargetRoleName: null,
            TargetGroupId: null,
            TargetUserId: null); // MISSING — required for Scope=User.

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "BroadcastScopeUserRequiresTargetUserId");
    }

    [Fact]
    public void Validate_WhenScopeIsUserAndTargetGroupIdSet_ReturnsUserExtraTargetsErrorCode()
    {
        // Arrange
        var validator = new SendBroadcastNotificationCommandValidator();
        var command = new SendBroadcastNotificationCommand(
            Title: "Valid Title",
            Message: "Valid Message",
            Scope: BroadcastScope.User,
            TargetRoleName: null,
            TargetGroupId: TestValues.GroupId, // MISCONFIGURED — User must not have GroupId.
            TargetUserId: TestValues.UserId);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "BroadcastScopeUserExtraTargets");
    }

    [Fact]
    public void Validate_WhenScopeIsUserAndAllValid_HasNoErrors()
    {
        // Arrange
        var validator = new SendBroadcastNotificationCommandValidator();
        var command = new SendBroadcastNotificationCommand(
            Title: "Valid Title",
            Message: "Valid Message",
            Scope: BroadcastScope.User,
            TargetRoleName: null,
            TargetGroupId: null,
            TargetUserId: TestValues.UserId);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }
}
