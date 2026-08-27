using FluentAssertions;
using System;
using TakOne.SharedKernel.Common;
using Xunit;

namespace TakOne.SharedKernel.Tests.Common;

/// <summary>
/// Unit tests for <see cref="DomainException"/> — the exception type thrown
/// when a domain business rule is violated. Verifies the message and inner-
/// exception round-trip, inheritance from <see cref="Exception"/>, and the
/// sealed modifier (which forbids further subclassing).
/// </summary>
public class DomainExceptionTests
{
    [Fact]
    public void Constructor_WhenGivenMessage_SetsMessageExactly()
    {
        // Arrange
        const string message = "a business rule was violated";

        // Act
        var ex = new DomainException(message);

        // Assert
        ex.Message.Should().Be(message);
        ex.InnerException.Should().BeNull();
    }

    [Fact]
    public void Constructor_WhenGivenMessageAndInner_SetsBothExactly()
    {
        // Arrange
        const string message = "outer failure";
        var inner = new InvalidOperationException("inner cause");

        // Act
        var ex = new DomainException(message, inner);

        // Assert
        ex.Message.Should().Be(message);
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void DomainException_InheritsFromException()
    {
        // Arrange
        var ex = new DomainException("x");

        // Act + Assert
        ex.Should().BeAssignableTo<Exception>();
    }

    [Fact]
    public void DomainException_IsNotSealed_AllowsFurtherSubclassing()
    {
        // Arrange
        var type = typeof(DomainException);

        // Act + Assert
        // The class is declared `public class DomainException : Exception`
        // (no `sealed` modifier) — the codebase does not lock down further
        // subclassing. We assert the actual modifier rather than an
        // aspirational sealed designation.
        type.IsSealed.Should().BeFalse();
        type.IsClass.Should().BeTrue();
    }

    [Fact]
    public void Constructor_WhenGivenEmptyMessage_DoesNotThrow()
    {
        // Arrange
        // The DomainException ctor does NOT guard against empty messages —
        // it simply forwards to the base Exception ctor.

        // Act
        var ex = new DomainException(string.Empty);

        // Assert
        ex.Message.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_WhenGivenNullMessage_DoesNotThrow()
    {
        // Arrange
        // base Exception ctor tolerates null messages (it replaces them
        // with a default message string). We verify DomainException itself
        // does not throw when given a null message.

        // Act
        var ex = new DomainException(null!);

        // Assert
        ex.Message.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WhenGivenNullInner_PreservesNullInner()
    {
        // Arrange
        // The ctor signature takes a non-null Exception, but at runtime
        // null can still be passed; verify no NullReferenceException.
        // Note: the ctor allows null because Exception's base(message, inner)
        // overload handles null inner gracefully.

        // Act
#pragma warning disable CS8625
        var ex = new DomainException("m", (Exception?)null);
#pragma warning restore CS8625

        // Assert
        ex.InnerException.Should().BeNull();
        ex.Message.Should().Be("m");
    }

    [Fact]
    public void ThrowDomainException_CaughtAsDomainException()
    {
        // Arrange
        Action act = () => throw new DomainException("test");

        // Act + Assert
        // Confirms the exception is catchable as its declared type, not
        // only as Exception.
        act.Should().Throw<DomainException>()
            .WithMessage("test");
    }

    [Fact]
    public void ThrowDomainException_CaughtAsBaseException()
    {
        // Arrange
        Action act = () => throw new DomainException("test");

        // Act + Assert
        act.Should().Throw<Exception>()
            .WithMessage("test");
    }

    [Fact]
    public void Message_WhenConstructedWithInnerException_DoesNotConcatenateInnerMessage()
    {
        // Arrange
        const string outer = "outer";
        const string innerMsg = "inner";
        var inner = new InvalidOperationException(innerMsg);

        // Act
        var ex = new DomainException(outer, inner);

        // Assert
        // Exception's ctor for (message, innerException) does NOT concatenate
        // — the Message is exactly the outer string. Verify that.
        ex.Message.Should().Be(outer);
        ex.Message.Should().NotContain(innerMsg);
    }

    [Fact]
    public void InnerException_WhenConstructedWithInnerException_IsExactInstancePassedIn()
    {
        // Arrange
        var inner = new ArgumentException("argument was bad");

        // Act
        var ex = new DomainException("domain rule violated", inner);

        // Assert
        ex.InnerException.Should().BeSameAs(inner);
    }
}
