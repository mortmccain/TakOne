using FluentAssertions;
using TakOne.SharedKernel.Primitives;
using TakOne.Testing;
using Xunit;

namespace TakOne.SharedKernel.Tests.Primitives;

/// <summary>
/// Unit tests for <see cref="BaseEntity"/> — the base class for all
/// entities. Verifies identity equality semantics (same Id == equal,
/// different Id != equal, type mismatch != equal), transient-entity
/// guards (random-Guid entities never compare equal), and the
/// matching GetHashCode override.
/// </summary>
public class BaseEntityTests
{
    // BaseEntity is abstract; tiny test subclasses for equality scenarios.
    private sealed class TestEntity : BaseEntity
    {
        public TestEntity() { }
        public TestEntity(Guid id) : base(id) { }
    }

    // A second entity TYPE — used to verify that two entities with the
    // same Id but different runtime types are NOT equal (different concepts).
    private sealed class OtherEntity : BaseEntity
    {
        public OtherEntity(Guid id) : base(id) { }
    }

    [Fact]
    public void ParameterlessConstructor_WhenCalled_AssignsNonEmptyGuid()
    {
        // Arrange + Act
        var e = new TestEntity();

        // Assert
        // The transient constructor calls Guid.NewGuid().
        e.Id.Should().NotBeEmpty();
        e.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Constructor_WhenGivenId_SetsIdToThatGuid()
    {
        // Arrange
        var id = TestValues.ProductId;

        // Act
        var e = new TestEntity(id);

        // Assert
        e.Id.Should().Be(id);
    }

    [Fact]
    public void ParameterlessConstructor_WhenTwoInstancesCreated_HaveDifferentIds()
    {
        // Arrange + Act
        var a = new TestEntity();
        var b = new TestEntity();

        // Assert
        // Each call to Guid.NewGuid() produces a unique value.
        a.Id.Should().NotBe(b.Id);
    }

    [Fact]
    public void Equals_WhenComparedToNull_ReturnsFalse()
    {
        // Arrange
        var e = new TestEntity(TestValues.ProductId);

        // Act
        var equal = e.Equals(null);

        // Assert
        equal.Should().BeFalse();
    }

    [Fact]
    public void Equals_WhenComparedToNullObject_ReturnsFalse()
    {
        // Arrange
        var e = new TestEntity(TestValues.ProductId);
        object? other = null;

        // Act
        var equal = e.Equals(other);

        // Assert
        equal.Should().BeFalse();
    }

    [Fact]
    public void Equals_WhenComparedToDifferentType_ReturnsFalse()
    {
        // Arrange
        var e = new TestEntity(TestValues.ProductId);
        var other = new OtherEntity(TestValues.ProductId); // SAME Id, different type

        // Act
        var equal = e.Equals(other);

        // Assert
        // Identity equality requires the EXACT same runtime type.
        equal.Should().BeFalse();
    }

    [Fact]
    public void Equals_WhenSameIdAndSameType_ReturnsTrue()
    {
        // Arrange
        var id = TestValues.CustomerId;
        var a = new TestEntity(id);
        var b = new TestEntity(id);

        // Act
        var equal = a.Equals(b);

        // Assert
        equal.Should().BeTrue();
    }

    [Fact]
    public void Equals_WhenDifferentIdAndSameType_ReturnsFalse()
    {
        // Arrange
        var a = new TestEntity(TestValues.CustomerId);
        var b = new TestEntity(TestValues.ProductId);

        // Act
        var equal = a.Equals(b);

        // Assert
        equal.Should().BeFalse();
    }

    [Fact]
    public void EqualsOperator_WhenSameId_ReturnsTrue()
    {
        // Arrange
        var id = TestValues.GroupId;
        var a = new TestEntity(id);
        var b = new TestEntity(id);

        // Act + Assert
        (a == b).Should().BeTrue();
        (a != b).Should().BeFalse();
    }

    [Fact]
    public void NotEqualsOperator_WhenDifferentId_ReturnsTrue()
    {
        // Arrange
        var a = new TestEntity(TestValues.GroupId);
        var b = new TestEntity(TestValues.GroupId2);

        // Act + Assert
        (a != b).Should().BeTrue();
        (a == b).Should().BeFalse();
    }

    [Fact]
    public void EqualsOperator_WhenLeftIsNull_ReturnsFalse()
    {
        // Arrange
        var a = (TestEntity?)null;
        var b = new TestEntity(TestValues.ProductId);

        // Act + Assert
        (a == b).Should().BeFalse();
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void EqualsOperator_WhenRightIsNull_ReturnsFalse()
    {
        // Arrange
        var a = new TestEntity(TestValues.ProductId);
        var b = (TestEntity?)null;

        // Act + Assert
        (a == b).Should().BeFalse();
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void EqualsOperator_WhenBothNull_ReturnsTrue()
    {
        // Arrange
        TestEntity? a = null;
        TestEntity? b = null;

        // Act + Assert
        (a == b).Should().BeTrue();
        (a != b).Should().BeFalse();
    }

    [Fact]
    public void Equals_WhenComparedToSelf_ReturnsTrue()
    {
        // Arrange
        var e = new TestEntity(TestValues.ProductId);

        // Act
        var equal = e.Equals(e);

        // Assert
        // ReferenceEquals shortcut path.
        equal.Should().BeTrue();
    }

    [Fact]
    public void GetHashCode_WhenCalledTwiceOnSameInstance_ReturnsSameValue()
    {
        // Arrange
        var e = new TestEntity(TestValues.ProductId);

        // Act
        var hash1 = e.GetHashCode();
        var hash2 = e.GetHashCode();

        // Assert
        hash1.Should().Be(hash2);
    }

    [Fact]
    public void GetHashCode_WhenTwoEntitiesWithSameId_ReturnsSameHash()
    {
        // Arrange
        var id = TestValues.ProductId;
        var a = new TestEntity(id);
        var b = new TestEntity(id);

        // Act + Assert
        // Required by the Equals contract: equal entities must produce
        // equal hash codes.
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void GetHashCode_WhenTwoEntitiesWithDifferentId_GenerallyReturnsDifferentHash()
    {
        // Arrange
        // Use fresh Guid.NewGuid values rather than the symmetric TestValues
        // Guids (aaaaaaaa-.../bbbbbb-...), which happen to hash to the same
        // 32-bit value via Guid.GetHashCode() — a hash-collision curiosity
        // that is mathematically allowed but flaky to assert on.
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var a = new TestEntity(id1);
        var b = new TestEntity(id2);

        // Defensive: ensure the Guids themselves differ (vanishingly unlikely
        // to be equal, but the test's premise depends on it).
        id1.Should().NotBe(id2);

        // Act + Assert
        // Different Id → generally different hash (modulo the kind of hash
        // collision we explicitly avoided by using random Guids).
        a.GetHashCode().Should().NotBe(b.GetHashCode());
    }

    [Fact]
    public void Equals_WhenBothIdsAreEmptyGuid_ReturnsFalse()
    {
        // Arrange
        // The empty-Guid guard makes the "transient" semantics honest —
        // two entities both marked with Guid.Empty must NEVER be equal even
        // though their Id values are technically equal.
        var a = new TestEntity(Guid.Empty);
        var b = new TestEntity(Guid.Empty);

        // Act
        var equal = a.Equals(b);

        // Assert
        equal.Should().BeFalse();
    }

    [Fact]
    public void EqualsOperator_WhenBothIdsAreEmptyGuid_ReturnsFalse()
    {
        // Arrange
        var a = new TestEntity(Guid.Empty);
        var b = new TestEntity(Guid.Empty);

        // Act + Assert
        (a == b).Should().BeFalse();
    }

    [Fact]
    public void GetHashCode_WhenIdIsEmptyGuid_MatchesGuidEmptyHashCode()
    {
        // Arrange
        var e = new TestEntity(Guid.Empty);

        // Act
        var hash = e.GetHashCode();

        // Assert
        // GetHashCode returns Id.GetHashCode() — for Guid.Empty this is
        // Guid.Empty.GetHashCode(). We assert consistency, not a specific
        // numeric value.
        hash.Should().Be(Guid.Empty.GetHashCode());
    }
}
