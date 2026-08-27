using FluentAssertions;
using TakOne.Domain.Customers.Entities;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;

namespace TakOne.Domain.Tests.Customers;

/// <summary>
/// Unit tests for the <see cref="CustomerGroup"/> aggregate root.
/// Verifies factory guards (name bounds, negative salary), Rename,
/// UpdateSalary, Activate/Deactivate (including same-value no-op behavior
/// that should NOT bump UpdatedAt), and the zero-salary edge case.
/// </summary>
public class CustomerGroupTests
{
    private static Money Irr(decimal amount) => new(amount, TestValues.IRR);

    // ======================================================================
    //                          CREATE — HAPPY PATH
    // ======================================================================

    [Fact]
    public void Create_WithValidNameAndSalary_ReturnsActiveGroupWithTimestamps()
    {
        // Arrange
        var before = DateTime.UtcNow;
        var salary = Irr(1_000_000m);

        // Act
        var group = CustomerGroup.Create("Management", salary);

        // Assert
        group.Id.Should().NotBeEmpty();
        group.Name.Should().Be("Management");
        group.Salary.Should().Be(salary);
        group.IsActive.Should().BeTrue();
        group.CreatedAt.Should().BeCloseTo(before, TimeSpan.FromSeconds(5));
        group.UpdatedAt.Should().Be(group.CreatedAt);
    }

    [Fact]
    public void Create_WithZeroSalary_IsAllowed()
    {
        // Arrange — zero salary is a valid "blocked" state (no purchases
        // allowed in SalaryOnly or Both mode; count-only mode still works).
        // Act
        var group = CustomerGroup.Create("Blocked", Money.Zero(TestValues.IRR));

        // Assert
        group.Salary.Amount.Should().Be(0m);
    }

    // ======================================================================
    //                          CREATE — GUARDS
    // ======================================================================

    [Fact]
    public void Create_WithEmptyName_Throws()
    {
        // Arrange
        Action act = () => CustomerGroup.Create("", Irr(1m));

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Customer group name is required.");
    }

    [Fact]
    public void Create_WithWhitespaceName_Throws()
    {
        // Arrange — IsNullOrWhiteSpace collapses whitespace
        Action act = () => CustomerGroup.Create("   ", Irr(1m));

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Customer group name is required.");
    }

    [Fact]
    public void Create_WithNameExceeding100Chars_Throws()
    {
        // Arrange — boundary violation: name length 101
        var longName = new string('a', 101);

        Action act = () => CustomerGroup.Create(longName, Irr(1m));

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Customer group name cannot exceed 100 characters.");
    }

    [Fact]
    public void Create_WithNegativeSalary_Throws()
    {
        // Arrange
        Action act = () => CustomerGroup.Create("Bad", Irr(-1m));

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Customer group salary cannot be negative.");
    }

    // ======================================================================
    //                          RENAME
    // ======================================================================

    [Fact]
    public void Rename_WithNewName_ChangesNameAndBumpsUpdatedAt()
    {
        // Arrange
        var group = CustomerGroup.Create("Old", Irr(1m));
        var originalUpdatedAt = group.UpdatedAt;

        // Act
        group.Rename("New");

        // Assert
        group.Name.Should().Be("New");
        group.UpdatedAt.Should().BeAfter(originalUpdatedAt);
    }

    [Fact]
    public void Rename_WithSameName_DoesNotBumpUpdatedAt()
    {
        // Arrange — short-circuit no-op when newName == Name
        var group = CustomerGroup.Create("Same", Irr(1m));
        var originalUpdatedAt = group.UpdatedAt;

        // Act
        group.Rename("Same");

        // Assert — UpdatedAt is unchanged (no spurious DB UPDATE generated)
        group.Name.Should().Be("Same");
        group.UpdatedAt.Should().Be(originalUpdatedAt);
    }

    [Fact]
    public void Rename_WithEmptyName_Throws()
    {
        // Arrange
        var group = CustomerGroup.Create("X", Irr(1m));

        // Act
        Action act = () => group.Rename("");

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Customer group name is required.");
    }

    [Fact]
    public void Rename_WithNameExceeding100_Throws()
    {
        // Arrange
        var group = CustomerGroup.Create("X", Irr(1m));
        var longName = new string('a', 101);

        // Act
        Action act = () => group.Rename(longName);

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Customer group name cannot exceed 100 characters.");
    }

    // ======================================================================
    //                          UPDATE SALARY
    // ======================================================================

    [Fact]
    public void UpdateSalary_WithNewSalary_ChangesSalaryAndBumpsUpdatedAt()
    {
        // Arrange
        var group = CustomerGroup.Create("X", Irr(1_000_000m));
        var originalUpdatedAt = group.UpdatedAt;

        // Act
        group.UpdateSalary(Irr(2_000_000m));

        // Assert
        group.Salary.Should().Be(Irr(2_000_000m));
        group.UpdatedAt.Should().BeAfter(originalUpdatedAt);
    }

    [Fact]
    public void UpdateSalary_WithSameSalary_DoesNotBumpUpdatedAt()
    {
        // Arrange — short-circuit no-op when newSalary == Salary (by value)
        var group = CustomerGroup.Create("X", Irr(1_000_000m));
        var originalUpdatedAt = group.UpdatedAt;

        // Act — pass the same Money value (new instance, but equal by value)
        group.UpdateSalary(new Money(1_000_000m, TestValues.IRR));

        // Assert
        group.UpdatedAt.Should().Be(originalUpdatedAt);
    }

    [Fact]
    public void UpdateSalary_WithNegativeSalary_Throws()
    {
        // Arrange
        var group = CustomerGroup.Create("X", Irr(1m));

        // Act
        Action act = () => group.UpdateSalary(Irr(-1m));

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Customer group salary cannot be negative.");
    }

    // ======================================================================
    //                          ACTIVATE / DEACTIVATE
    // ======================================================================

    [Fact]
    public void Deactivate_FromActive_SetsInactiveAndBumpsUpdatedAt()
    {
        // Arrange
        var group = CustomerGroup.Create("X", Irr(1m));
        var originalUpdatedAt = group.UpdatedAt;

        // Act
        group.Deactivate();

        // Assert
        group.IsActive.Should().BeFalse();
        group.UpdatedAt.Should().BeAfter(originalUpdatedAt);
    }

    [Fact]
    public void Deactivate_FromInactive_IsNoOpAndDoesNotBumpUpdatedAt()
    {
        // Arrange — already inactive; Deactivate returns early
        var group = CustomerGroup.Create("X", Irr(1m));
        group.Deactivate();
        var inactiveUpdatedAt = group.UpdatedAt;

        // Act
        group.Deactivate();

        // Assert
        group.IsActive.Should().BeFalse();
        group.UpdatedAt.Should().Be(inactiveUpdatedAt);
    }

    [Fact]
    public void Activate_FromInactive_SetsActiveAndBumpsUpdatedAt()
    {
        // Arrange
        var group = CustomerGroup.Create("X", Irr(1m));
        group.Deactivate();
        var inactiveUpdatedAt = group.UpdatedAt;

        // Act
        group.Activate();

        // Assert
        group.IsActive.Should().BeTrue();
        group.UpdatedAt.Should().BeAfter(inactiveUpdatedAt);
    }

    [Fact]
    public void Activate_FromActive_IsNoOpAndDoesNotBumpUpdatedAt()
    {
        // Arrange — already active; Activate returns early
        var group = CustomerGroup.Create("X", Irr(1m));
        var originalUpdatedAt = group.UpdatedAt;

        // Act
        group.Activate();

        // Assert
        group.IsActive.Should().BeTrue();
        group.UpdatedAt.Should().Be(originalUpdatedAt);
    }
}
