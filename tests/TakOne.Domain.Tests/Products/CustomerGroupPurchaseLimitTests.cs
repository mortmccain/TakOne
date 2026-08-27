using FluentAssertions;
using TakOne.Domain.Products.ValueObjects;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using Xunit;

namespace TakOne.Domain.Tests.Products;

/// <summary>
/// Unit tests for the <see cref="CustomerGroupPurchaseLimit"/> value object.
/// Verifies the factory's GroupId + Limit guards, the DefaultLimit constant,
/// equality semantics, ToString, and the == / != operators.
/// </summary>
public class CustomerGroupPurchaseLimitTests
{
    // ======================================================================
    //                          CREATE — HAPPY PATH
    // ======================================================================

    [Fact]
    public void Create_WithValidGroupIdAndLimit_SetsBothProperties()
    {
        // Arrange — choose a valid limit ≥ 1
        // Act
        var limit = CustomerGroupPurchaseLimit.Create(TestValues.GroupId, 5);

        // Assert
        limit.GroupId.Should().Be(TestValues.GroupId);
        limit.Limit.Should().Be(5);
    }

    [Fact]
    public void Create_WithMinimumValidLimitOne_Works()
    {
        // Arrange — boundary: limit=1 is the smallest legal value
        // Act
        var limit = CustomerGroupPurchaseLimit.Create(TestValues.GroupId, 1);

        // Assert
        limit.Limit.Should().Be(1);
    }

    [Fact]
    public void Create_WithLargeValidLimit_Works()
    {
        // Arrange — boundary: a large but well-formed limit value
        // Act
        var limit = CustomerGroupPurchaseLimit.Create(TestValues.GroupId, 10000);

        // Assert
        limit.Limit.Should().Be(10000);
    }

    // ======================================================================
    //                          CREATE — GUARDS
    // ======================================================================

    [Fact]
    public void Create_WithEmptyGroupId_Throws()
    {
        // Arrange
        Action act = () => CustomerGroupPurchaseLimit.Create(Guid.Empty, 5);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Group Id is required for a purchase limit.");
    }

    [Fact]
    public void Create_WithLimitLessThanOne_Throws()
    {
        // Arrange
        Action act = () => CustomerGroupPurchaseLimit.Create(TestValues.GroupId, 0);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Purchase limit must be at least 1.");
    }

    [Fact]
    public void Create_WithNegativeLimit_Throws()
    {
        // Arrange — defense-in-depth: negative limit makes no sense
        Action act = () => CustomerGroupPurchaseLimit.Create(TestValues.GroupId, -3);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Purchase limit must be at least 1.");
    }

    // ======================================================================
    //                          CONSTANT
    // ======================================================================

    [Fact]
    public void DefaultLimit_IsOne()
    {
        // Assert — the business rule: every newly-created limit starts at 1
        CustomerGroupPurchaseLimit.DefaultLimit.Should().Be(1);
    }

    // ======================================================================
    //                          EQUALITY
    // ======================================================================

    [Fact]
    public void Equality_WhenSameGroupIdAndLimit_AreEqual()
    {
        // Arrange — value-object equality is by (GroupId, Limit)
        var a = CustomerGroupPurchaseLimit.Create(TestValues.GroupId, 5);
        var b = CustomerGroupPurchaseLimit.Create(TestValues.GroupId, 5);

        // Assert
        a.Should().Be(b);
        a.Equals(b).Should().BeTrue();
        (a == b).Should().BeTrue();
        (a != b).Should().BeFalse();
    }

    [Fact]
    public void Equality_WhenDifferentGroupId_AreNotEqual()
    {
        // Arrange
        var a = CustomerGroupPurchaseLimit.Create(TestValues.GroupId, 5);
        var b = CustomerGroupPurchaseLimit.Create(TestValues.GroupId2, 5);

        // Assert
        a.Should().NotBe(b);
        (a == b).Should().BeFalse();
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void Equality_WhenSameGroupIdButDifferentLimit_AreNotEqual()
    {
        // Arrange — same group, different limit → different value object
        var a = CustomerGroupPurchaseLimit.Create(TestValues.GroupId, 5);
        var b = CustomerGroupPurchaseLimit.Create(TestValues.GroupId, 10);

        // Assert
        a.Should().NotBe(b);
        (a == b).Should().BeFalse();
    }

    [Fact]
    public void GetHashCode_WhenEqualObjects_ProduceSameHash()
    {
        // Arrange
        var a = CustomerGroupPurchaseLimit.Create(TestValues.GroupId, 5);
        var b = CustomerGroupPurchaseLimit.Create(TestValues.GroupId, 5);

        // Assert — hash code must match for equal value objects
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    // ======================================================================
    //                          TOSTRING
    // ======================================================================

    [Fact]
    public void ToString_ReturnsGroupAndLimit()
    {
        // Arrange
        var limit = CustomerGroupPurchaseLimit.Create(TestValues.GroupId, 7);

        // Act
        var s = limit.ToString();

        // Assert — format is "Group {GroupId}: {Limit}"
        s.Should().Be($"Group {TestValues.GroupId}: 7");
    }

    // ======================================================================
    //                          NULL/TYPE EQUALITY
    // ======================================================================

    [Fact]
    public void Equals_WhenComparedToNull_ReturnsFalse()
    {
        // Arrange
        var limit = CustomerGroupPurchaseLimit.Create(TestValues.GroupId, 5);

        // Assert
        limit.Equals(null).Should().BeFalse();
    }

    [Fact]
    public void Equals_WhenComparedToDifferentType_ReturnsFalse()
    {
        // Arrange — passing an unrelated object to Equals should be false, not throw
        var limit = CustomerGroupPurchaseLimit.Create(TestValues.GroupId, 5);

        // Assert
        limit.Equals("not a limit").Should().BeFalse();
    }

    [Fact]
    public void Equality_WhenLeftOperandIsNull_ReturnsFalseForOperator()
    {
        // Arrange — the == operator handles null left operand
        CustomerGroupPurchaseLimit? left = null;
        var right = CustomerGroupPurchaseLimit.Create(TestValues.GroupId, 5);

#pragma warning disable CS8604 // Possible null reference argument — the operator
                               // signature is non-nullable but the implementation handles null
                               // operands internally (mirrors the 16-A MoneyTests pattern).
        // Assert
        (left == right).Should().BeFalse();
        (left != right).Should().BeTrue();
#pragma warning restore CS8604
    }
}
