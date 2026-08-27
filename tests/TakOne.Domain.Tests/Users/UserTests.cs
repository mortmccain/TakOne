using FluentAssertions;
using TakOne.Domain.Users;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using Xunit;

// Note: Gender lives at namespace TakOne.Domain.Users (not TakOne.Domain.Users.Enums)
// even though the file path is Users/Enums/Gender.cs. The file-scoped namespace
// declaration in the SUT is `namespace TakOne.Domain.Users;`.

namespace TakOne.Domain.Tests.Users;

/// <summary>
/// Unit tests for the <see cref="User"/> aggregate root. Verifies
/// CreateCustomer vs CreateStaff factory distinction, AssignToGroup,
/// RemoveFromGroup, ChangeFullName, ChangeGender (including the
/// undefined-enum-value guard), and Activate/Deactivate.
/// </summary>
public class UserTests
{
    // ======================================================================
    //                          CreateCustomer
    // ======================================================================

    [Fact]
    public void CreateCustomer_WithValidArgs_ReturnsActiveCustomerWithGroupAndDefaultMaleGender()
    {
        // Arrange
        const string workerId = "EMP-001";
        const string fullName = "Alice";

        // Act
        var user = User.CreateCustomer(workerId, fullName, TestValues.GroupId);

        // Assert
        user.Id.Should().NotBeEmpty();
        user.WorkerId.Should().Be(workerId);
        user.FullName.Should().Be(fullName);
        user.GroupId.Should().Be(TestValues.GroupId);
        user.IsActive.Should().BeTrue();
        user.Gender.Should().Be(Gender.Male); // default when not specified
    }

    [Fact]
    public void CreateCustomer_WithExplicitFemaleGender_SetsGenderFemale()
    {
        // Act
        var user = User.CreateCustomer("EMP-001", "Alice", TestValues.GroupId, Gender.Female);

        // Assert
        user.Gender.Should().Be(Gender.Female);
    }

    [Fact]
    public void CreateCustomer_WithEmptyWorkerId_Throws()
    {
        Action act = () => User.CreateCustomer("", "Alice", TestValues.GroupId);

        act.Should().Throw<DomainException>().WithMessage("Worker ID is required.");
    }

    [Fact]
    public void CreateCustomer_WithWhitespaceWorkerId_Throws()
    {
        Action act = () => User.CreateCustomer("   ", "Alice", TestValues.GroupId);

        act.Should().Throw<DomainException>().WithMessage("Worker ID is required.");
    }

    [Fact]
    public void CreateCustomer_WithWorkerIdExceeding100Chars_Throws()
    {
        var longWorkerId = new string('x', 101);

        Action act = () => User.CreateCustomer(longWorkerId, "Alice", TestValues.GroupId);

        act.Should().Throw<DomainException>().WithMessage("Worker ID cannot exceed 100 characters.");
    }

    [Fact]
    public void CreateCustomer_WithEmptyFullName_Throws()
    {
        Action act = () => User.CreateCustomer("EMP-001", "", TestValues.GroupId);

        act.Should().Throw<DomainException>().WithMessage("Full name is required.");
    }

    [Fact]
    public void CreateCustomer_WithFullNameExceeding200Chars_Throws()
    {
        var longName = new string('x', 201);

        Action act = () => User.CreateCustomer("EMP-001", longName, TestValues.GroupId);

        act.Should().Throw<DomainException>().WithMessage("Full name cannot exceed 200 characters.");
    }

    [Fact]
    public void CreateCustomer_WithEmptyGroupId_Throws()
    {
        Action act = () => User.CreateCustomer("EMP-001", "Alice", Guid.Empty);

        act.Should().Throw<DomainException>().WithMessage("Customer group Id is required for customers.");
    }

    // ======================================================================
    //                          CreateStaff
    // ======================================================================

    [Fact]
    public void CreateStaff_WithValidArgs_ReturnsActiveStaffWithNullGroupAndDefaultMaleGender()
    {
        // Act
        var user = User.CreateStaff("EMP-002", "Bob");

        // Assert
        user.WorkerId.Should().Be("EMP-002");
        user.FullName.Should().Be("Bob");
        user.GroupId.Should().BeNull(); // staff have no group
        user.IsActive.Should().BeTrue();
        user.Gender.Should().Be(Gender.Male); // default
    }

    [Fact]
    public void CreateStaff_WithFemaleGender_SetsGenderFemale()
    {
        // Act
        var user = User.CreateStaff("EMP-002", "Bob", Gender.Female);

        // Assert
        user.Gender.Should().Be(Gender.Female);
    }

    [Fact]
    public void CreateStaff_VsCreateCustomer_GroupIdDiffers()
    {
        // Act
        var customer = User.CreateCustomer("EMP-001", "A", TestValues.GroupId);
        var staff = User.CreateStaff("EMP-002", "B");

        // Assert
        customer.GroupId.Should().NotBeNull();
        staff.GroupId.Should().BeNull();
    }

    // ======================================================================
    //                          AssignToGroup / RemoveFromGroup
    // ======================================================================

    [Fact]
    public void AssignToGroup_WithValidGroupId_SetsGroupId()
    {
        // Arrange — start with a staff user (no group)
        var user = User.CreateStaff("EMP-002", "Bob");

        // Act
        user.AssignToGroup(TestValues.GroupId);

        // Assert
        user.GroupId.Should().Be(TestValues.GroupId);
    }

    [Fact]
    public void AssignToGroup_WithEmptyGroupId_Throws()
    {
        // Arrange
        var user = User.CreateStaff("EMP-002", "Bob");

        // Act
        Action act = () => user.AssignToGroup(Guid.Empty);

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Customer group Id is required for customers.");
    }

    [Fact]
    public void RemoveFromGroup_SetsGroupIdToNull()
    {
        // Arrange — start with a customer (has a group)
        var user = User.CreateCustomer("EMP-001", "Alice", TestValues.GroupId);

        // Act
        user.RemoveFromGroup();

        // Assert
        user.GroupId.Should().BeNull();
    }

    // ======================================================================
    //                          ChangeFullName
    // ======================================================================

    [Fact]
    public void ChangeFullName_WithValidName_UpdatesFullName()
    {
        // Arrange
        var user = User.CreateStaff("EMP-001", "Old Name");

        // Act
        user.ChangeFullName("New Name");

        // Assert
        user.FullName.Should().Be("New Name");
    }

    [Fact]
    public void ChangeFullName_WithEmpty_Throws()
    {
        // Arrange
        var user = User.CreateStaff("EMP-001", "X");

        // Act
        Action act = () => user.ChangeFullName("");

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Full name is required.");
    }

    [Fact]
    public void ChangeFullName_WithExceeding200Chars_Throws()
    {
        // Arrange
        var user = User.CreateStaff("EMP-001", "X");
        var longName = new string('x', 201);

        // Act
        Action act = () => user.ChangeFullName(longName);

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Full name cannot exceed 200 characters.");
    }

    // ======================================================================
    //                          ChangeGender
    // ======================================================================

    [Fact]
    public void ChangeGender_WithValidGender_UpdatesGender()
    {
        // Arrange — start Male
        var user = User.CreateStaff("EMP-001", "Bob", Gender.Male);

        // Act
        user.ChangeGender(Gender.Female);

        // Assert
        user.Gender.Should().Be(Gender.Female);
    }

    [Fact]
    public void ChangeGender_WithUndefinedEnumValue_Throws()
    {
        // Arrange — start Male; try to set an out-of-range enum value
        var user = User.CreateStaff("EMP-001", "Bob", Gender.Male);

        // Act — (Gender)42 is not a defined enum member
        Action act = () => user.ChangeGender((Gender)42);

        // Assert — the message format lists the defined enum names
        act.Should().Throw<DomainException>()
            .WithMessage("Gender must be one of: Male, Female.");
    }

    // ======================================================================
    //                          Activate / Deactivate
    // ======================================================================

    [Fact]
    public void Deactivate_SetsIsActiveFalse()
    {
        // Arrange
        var user = User.CreateStaff("EMP-001", "Bob");

        // Act
        user.Deactivate();

        // Assert
        user.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Activate_SetsIsActiveTrue()
    {
        // Arrange — start inactive
        var user = User.CreateStaff("EMP-001", "Bob");
        user.Deactivate();

        // Act
        user.Activate();

        // Assert
        user.IsActive.Should().BeTrue();
    }
}
