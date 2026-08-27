using FluentAssertions;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.Primitives;
using TakOne.Testing;
using Xunit;

namespace TakOne.SharedKernel.Tests.Primitives;

/// <summary>
/// Unit tests for <see cref="AggregateRoot"/> — the base class for DDD
/// aggregate roots. Verifies the Id construction contracts, the domain-
/// event collection's add / clear / remove semantics, and that the
/// collection is exposed as a read-only projection.
/// </summary>
public class AggregateRootTests
{
    // AggregateRoot is abstract and AddDomainEvent is protected; we
    // instantiate a tiny subclass that exposes Add via a public proxy.
    private sealed class TestAggregate : AggregateRoot
    {
        public TestAggregate() { }
        public TestAggregate(Guid id) : base(id) { }

        public new void AddDomainEvent(BaseDomainEvent domainEvent) => base.AddDomainEvent(domainEvent);
    }

    // Smallest concrete BaseDomainEvent subclass for tests.
    private sealed class TestEvent : BaseDomainEvent { }

    [Fact]
    public void Constructor_WhenGivenGuid_SetsIdToThatGuid()
    {
        // Arrange
        var id = TestValues.SaleId;

        // Act
        var ar = new TestAggregate(id);

        // Assert
        ar.Id.Should().Be(id);
    }

    [Fact]
    public void DefaultConstructor_WhenCalled_AssignsNonEmptyRandomGuid()
    {
        // Arrange + Act
        var ar = new TestAggregate();

        // Assert
        ar.Id.Should().NotBeEmpty();
        ar.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void DefaultConstructor_WhenTwoInstancesCreated_HaveDifferentIds()
    {
        // Arrange + Act
        var a = new TestAggregate();
        var b = new TestAggregate();

        // Assert
        a.Id.Should().NotBe(b.Id);
    }

    [Fact]
    public void DomainEvents_WhenNewlyConstructed_IsEmpty()
    {
        // Arrange + Act
        var ar = new TestAggregate();

        // Assert
        ar.DomainEvents.Should().BeEmpty();
        ar.DomainEvents.Count.Should().Be(0);
    }

    [Fact]
    public void AddDomainEvent_WhenCalled_AddsEventToCollection()
    {
        // Arrange
        var ar = new TestAggregate();
        var e = new TestEvent();

        // Act
        ar.AddDomainEvent(e);

        // Assert
        ar.DomainEvents.Should().Contain(e);
        ar.DomainEvents.Count.Should().Be(1);
    }

    [Fact]
    public void AddDomainEvent_WhenSameEventInstanceAddedTwice_DoesNotDedupe()
    {
        // Arrange
        // The base class uses List<T>.Add which does NOT deduplicate.
        var ar = new TestAggregate();
        var e = new TestEvent();

        // Act
        ar.AddDomainEvent(e);
        ar.AddDomainEvent(e);

        // Assert
        ar.DomainEvents.Count.Should().Be(2);
        ar.DomainEvents.Should().Contain(e);
    }

    [Fact]
    public void AddDomainEvent_WhenDifferentEventsAdded_PreservesInsertionOrder()
    {
        // Arrange
        var ar = new TestAggregate();
        var e1 = new TestEvent();
        var e2 = new TestEvent();
        var e3 = new TestEvent();

        // Act
        ar.AddDomainEvent(e1);
        ar.AddDomainEvent(e2);
        ar.AddDomainEvent(e3);

        // Assert
        ar.DomainEvents.Should().ContainInOrder(e1, e2, e3);
    }

    [Fact]
    public void ClearDomainEvents_WhenCalled_EmptiesCollection()
    {
        // Arrange
        var ar = new TestAggregate();
        ar.AddDomainEvent(new TestEvent());
        ar.AddDomainEvent(new TestEvent());

        // Act
        ar.ClearDomainEvents();

        // Assert
        ar.DomainEvents.Should().BeEmpty();
        ar.DomainEvents.Count.Should().Be(0);
    }

    [Fact]
    public void ClearDomainEvents_WhenCalledOnEmptyCollection_NoOp()
    {
        // Arrange
        var ar = new TestAggregate();

        // Act
        ar.ClearDomainEvents();

        // Assert
        ar.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void RemoveDomainEvents_WhenGivenSubset_RemovesOnlyThoseEvents()
    {
        // Arrange
        var ar = new TestAggregate();
        var e1 = new TestEvent();
        var e2 = new TestEvent();
        var e3 = new TestEvent();
        ar.AddDomainEvent(e1);
        ar.AddDomainEvent(e2);
        ar.AddDomainEvent(e3);

        // Act
        ar.RemoveDomainEvents(new[] { e1, e3 });

        // Assert
        ar.DomainEvents.Should().ContainSingle().Which.Should().BeSameAs(e2);
        ar.DomainEvents.Count.Should().Be(1);
    }

    [Fact]
    public void RemoveDomainEvents_WhenGivenUnknownEvent_NoOp()
    {
        // Arrange
        var ar = new TestAggregate();
        var known = new TestEvent();
        var unknown = new TestEvent();
        ar.AddDomainEvent(known);

        // Act
        ar.RemoveDomainEvents(new[] { unknown });

        // Assert
        // List<T>.Remove silently no-ops when the element is not present.
        ar.DomainEvents.Should().ContainSingle().Which.Should().BeSameAs(known);
    }

    [Fact]
    public void RemoveDomainEvents_WhenGivenEmptyEnumerable_NoOp()
    {
        // Arrange
        var ar = new TestAggregate();
        var e = new TestEvent();
        ar.AddDomainEvent(e);

        // Act
        ar.RemoveDomainEvents(Array.Empty<BaseDomainEvent>());

        // Assert
        ar.DomainEvents.Should().ContainSingle();
    }

    [Fact]
    public void RemoveDomainEvents_WhenCalledAfterClear_NoOp()
    {
        // Arrange
        var ar = new TestAggregate();
        var e = new TestEvent();
        ar.AddDomainEvent(e);
        ar.ClearDomainEvents();

        // Act
        ar.RemoveDomainEvents(new[] { e });

        // Assert
        ar.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void AddDomainEvent_AfterClearDomainEvents_CanStillAddEvents()
    {
        // Arrange
        var ar = new TestAggregate();
        ar.AddDomainEvent(new TestEvent());
        ar.ClearDomainEvents();
        var e = new TestEvent();

        // Act
        ar.AddDomainEvent(e);

        // Assert
        ar.DomainEvents.Should().ContainSingle().Which.Should().BeSameAs(e);
    }

    [Fact]
    public void DomainEvents_ReturnsReadOnlyCollectionProjection()
    {
        // Arrange
        var ar = new TestAggregate();

        // Act
        var firstRead = ar.DomainEvents;
        var secondRead = ar.DomainEvents;

        // Assert
        // The property returns _domainEvents.AsReadOnly() — each access is a
        // new wrapper instance, but both reflect the same underlying list.
        firstRead.Should().NotBeSameAs(secondRead);
        firstRead.Should().BeEquivalentTo(secondRead);
    }
}
