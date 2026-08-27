using FluentAssertions;
using TakOne.SharedKernel.Primitives;
using Xunit;

namespace TakOne.SharedKernel.Tests.Primitives;

/// <summary>
/// Unit tests for <see cref="BaseValueObject"/> — the base class for DDD
/// value objects. Verifies component-based equality, the matching
/// <see cref="BaseValueObject.GetHashCode"/> override, the == and !=
/// operators (including null handling), and type-mismatch rejection.
/// </summary>
public class BaseValueObjectTests
{
    // Tiny concrete value object with two components.
    private sealed class TestValueObject : BaseValueObject
    {
        public int Number { get; }
        public string Text { get; }

        public TestValueObject(int number, string text)
        {
            Number = number;
            Text = text;
        }

        protected override IEnumerable<object> GetEqualityComponents()
        {
            yield return Number;
            yield return Text;
        }
    }

    [Fact]
    public void Equals_WhenComponentsAreSame_ReturnsTrue()
    {
        // Arrange
        var a = new TestValueObject(1, "abc");
        var b = new TestValueObject(1, "abc");

        // Act
        var equal = a.Equals(b);

        // Assert
        equal.Should().BeTrue();
    }

    [Fact]
    public void Equals_WhenComponentsAreDifferent_ReturnsFalse()
    {
        // Arrange
        var a = new TestValueObject(1, "abc");
        var b = new TestValueObject(1, "xyz"); // same Number, different Text

        // Act
        var equal = a.Equals(b);

        // Assert
        equal.Should().BeFalse();
    }

    [Fact]
    public void Equals_WhenFirstComponentDifferent_ReturnsFalse()
    {
        // Arrange
        var a = new TestValueObject(1, "abc");
        var b = new TestValueObject(2, "abc"); // different Number, same Text

        // Act
        var equal = a.Equals(b);

        // Assert
        equal.Should().BeFalse();
    }

    [Fact]
    public void EqualsOperator_WhenComponentsAreSame_ReturnsTrue()
    {
        // Arrange
        var a = new TestValueObject(1, "abc");
        var b = new TestValueObject(1, "abc");

        // Act + Assert
        (a == b).Should().BeTrue();
        (a != b).Should().BeFalse();
    }

    [Fact]
    public void NotEqualsOperator_WhenComponentsDifferent_ReturnsTrue()
    {
        // Arrange
        var a = new TestValueObject(1, "abc");
        var b = new TestValueObject(2, "abc");

        // Act + Assert
        (a != b).Should().BeTrue();
        (a == b).Should().BeFalse();
    }

    [Fact]
    public void Equals_WhenComparedToNull_ReturnsFalse()
    {
        // Arrange
        var a = new TestValueObject(1, "abc");

        // Act
        var equal = a.Equals(null);

        // Assert
        equal.Should().BeFalse();
    }

    [Fact]
    public void Equals_WhenComparedToObjectOfDifferentType_ReturnsFalse()
    {
        // Arrange
        var a = new TestValueObject(1, "abc");
        object other = "not a value object";

        // Act
        var equal = a.Equals(other);

        // Assert
        // The Equals implementation checks GetType() equality first.
        equal.Should().BeFalse();
    }

    [Fact]
    public void EqualsOperator_WhenBothOperandsNull_ReturnsTrue()
    {
        // Arrange
        TestValueObject? a = null;
        TestValueObject? b = null;

        // Act + Assert
        // The == operator handles the both-null case as a special branch.
#pragma warning disable CS8604 // Operator signature is non-nullable but implementation handles null.
        (a == b).Should().BeTrue();
#pragma warning restore CS8604
    }

    [Fact]
    public void EqualsOperator_WhenLeftIsNull_ReturnsFalse()
    {
        // Arrange
        TestValueObject? a = null;
        var b = new TestValueObject(1, "abc");

        // Act + Assert
#pragma warning disable CS8604 // Operator signature is non-nullable but implementation handles null.
        (a == b).Should().BeFalse();
#pragma warning restore CS8604
    }

    [Fact]
    public void EqualsOperator_WhenRightIsNull_ReturnsFalse()
    {
        // Arrange
        var a = new TestValueObject(1, "abc");
        TestValueObject? b = null;

        // Act + Assert
#pragma warning disable CS8604 // Operator signature is non-nullable but implementation handles null.
        (a == b).Should().BeFalse();
#pragma warning restore CS8604
    }

    [Fact]
    public void GetHashCode_WhenCalledTwice_ReturnsSameValue()
    {
        // Arrange
        var a = new TestValueObject(1, "abc");

        // Act
        var hash1 = a.GetHashCode();
        var hash2 = a.GetHashCode();

        // Assert
        hash1.Should().Be(hash2);
    }

    [Fact]
    public void GetHashCode_WhenTwoInstancesWithSameComponents_ReturnsSameHash()
    {
        // Arrange
        var a = new TestValueObject(1, "abc");
        var b = new TestValueObject(1, "abc");

        // Act + Assert
        // Required by Equals contract.
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void GetHashCode_WhenTwoInstancesWithDifferentComponents_MayDiffer()
    {
        // Arrange
        var a = new TestValueObject(1, "abc");
        var b = new TestValueObject(2, "xyz");

        // Act + Assert
        // Hash is computed from components via XOR. Different components
        // will (modulo astronomically unlikely collision) produce different
        // hash codes.
        a.GetHashCode().Should().NotBe(b.GetHashCode());
    }

    [Fact]
    public void Equals_WhenPassedSubclassInstance_ReturnsFalse()
    {
        // Arrange
        // A second subclass type with the same components — should NOT be
        // equal because Equals checks GetType() equality.
        var a = new TestValueObject(1, "abc");
        var b = (object)new DerivedValueObject(1, "abc");

        // Act
        var equal = a.Equals(b);

        // Assert
        equal.Should().BeFalse();
    }

    private sealed class DerivedValueObject : BaseValueObject
    {
        private readonly int _number;
        private readonly string _text;

        public DerivedValueObject(int number, string text)
        {
            _number = number;
            _text = text;
        }

        protected override IEnumerable<object> GetEqualityComponents()
        {
            yield return _number;
            yield return _text;
        }
    }
}
