using FluentAssertions;
using TakOne.SharedKernel.Common;
using Xunit;

namespace TakOne.SharedKernel.Tests.Common;

/// <summary>
/// Unit tests for <see cref="BaseDomainEvent"/> — the abstract base class
/// for all domain events. Verifies the auto-assigned <see cref="BaseDomainEvent.EventId"/>
/// and <see cref="BaseDomainEvent.OccurredOn"/> fields and uniqueness
/// semantics across instances.
/// </summary>
public class BaseDomainEventTests
{
    // BaseDomainEvent is abstract; we instantiate a tiny subclass for tests.
    private sealed class TestEvent : BaseDomainEvent { }

    [Fact]
    public void EventId_WhenConstructed_IsNonEmptyGuid()
    {
        // Arrange + Act
        var e = new TestEvent();

        // Assert
        e.EventId.Should().NotBeEmpty();
        e.EventId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void OccurredOn_WhenConstructed_IsCloseToUtcNow()
    {
        // Arrange
        var before = DateTime.UtcNow;

        // Act
        var e = new TestEvent();

        // Assert
        // The auto-assigned OccurredOn should be close to UtcNow. We allow a
        // generous tolerance because tests can be slow under load.
        var after = DateTime.UtcNow;
        e.OccurredOn.Should().BeOnOrAfter(before - TimeSpan.FromSeconds(1));
        e.OccurredOn.Should().BeOnOrBefore(after + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void EventId_WhenTwoInstancesCreated_AreDifferent()
    {
        // Arrange + Act
        var a = new TestEvent();
        var b = new TestEvent();

        // Assert
        a.EventId.Should().NotBe(b.EventId);
    }

    [Fact]
    public void OccurredOn_WhenTwoInstancesCreatedInSequence_SecondIsOnOrAfterFirst()
    {
        // Arrange + Act
        var a = new TestEvent();
        var b = new TestEvent();

        // Assert
        b.OccurredOn.Should().BeOnOrAfter(a.OccurredOn);
    }

    [Fact]
    public void BaseDomainEvent_IsAbstract()
    {
        // Arrange
        var type = typeof(BaseDomainEvent);

        // Act + Assert
        // The class is abstract so it cannot be instantiated directly —
        // consumers must derive a concrete event subclass.
        type.IsAbstract.Should().BeTrue();
    }

    [Fact]
    public void EventId_IsExactly16Bytes()
    {
        // Arrange
        var e = new TestEvent();

        // Act
        var bytes = e.EventId.ToByteArray();

        // Assert
        bytes.Should().HaveCount(16);
    }

    [Fact]
    public void OccurredOn_Kind_IsUtcOrLocalConvertedToUtcRoundtrip()
    {
        // Arrange
        var e = new TestEvent();

        // Act + Assert
        // The source uses DateTime.UtcNow, so Kind is Utc.
        e.OccurredOn.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void EventId_WhenReadMultipleTimes_ReturnsSameValue()
    {
        // Arrange
        var e = new TestEvent();

        // Act
        var first = e.EventId;
        var second = e.EventId;

        // Assert
        // The property is get-only and assigned at construction; reads must
        // be stable across accesses.
        second.Should().Be(first);
    }

    [Fact]
    public void OccurredOn_WhenReadMultipleTimes_ReturnsSameValue()
    {
        // Arrange
        var e = new TestEvent();

        // Act
        var first = e.OccurredOn;
        var second = e.OccurredOn;

        // Assert
        second.Should().Be(first);
    }
}
