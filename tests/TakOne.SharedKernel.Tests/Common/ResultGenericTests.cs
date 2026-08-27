using FluentAssertions;
using TakOne.SharedKernel.Common;
using Xunit;

namespace TakOne.SharedKernel.Tests.Common;

/// <summary>
/// Unit tests for <see cref="Result{T}"/> — the value-bearing generic
/// Result. Verifies the Success / Failure factory contracts, the value
/// round-trip, and the InvalidOperationException guards inherited from
/// the base <see cref="Result"/> constructor.
/// </summary>
public class ResultGenericTests
{
    // The Result<T> ctor is `protected internal`, so we expose it through a
    // tiny test subclass. (`protected internal` = `protected OR internal`,
    // so a derived class in another assembly can still call the ctor.)
    private sealed class TestResult<T> : Result<T>
    {
        public TestResult(T value, bool isSuccess, string error)
            : base(value, isSuccess, error) { }
    }

    [Fact]
    public void Success_WhenGivenValue_ReturnsIsSuccessTrueWithValueAndEmptyError()
    {
        // Arrange
        const int value = 42;

        // Act
        var result = Result<int>.Success(value);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Value.Should().Be(value);
        result.Error.Should().BeEmpty();
    }

    [Fact]
    public void Failure_WhenGivenError_ReturnsIsFailureTrueWithDefaultValue()
    {
        // Arrange
        const string error = "kaboom";

        // Act
        var result = Result<int>.Failure(error);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);
        result.Value.Should().Be(default(int));
    }

    [Fact]
    public void Failure_WhenGivenErrorForReferenceType_ReturnsNullValue()
    {
        // Arrange
        const string error = "missing";

        // Act
        var result = Result<string>.Failure(error);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(error);
        // For reference-type T, default! is null.
        result.Value.Should().BeNull();
    }

    [Fact]
    public void Failure_HidesParentFailureMethodAndReturnsResultT()
    {
        // Arrange
        const string error = "hidden";

        // Act
        // Call Result<T>.Failure explicitly — this exercises the `new`
        // hiding modifier on the static Failure method.
        Result<string> result = Result<string>.Failure(error);

        // Assert
        // The result is a Result<string>, not a plain Result — the static
        // method's return type is the generic class itself.
        result.Should().BeOfType<Result<string>>();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);
    }

    [Fact]
    public void Success_WhenGivenNullReferenceType_ReturnsIsSuccessTrueWithNullValue()
    {
        // Arrange
        // Result<T>.Success does NOT guard against null values for
        // reference-type T — passing null produces a Success with a null Value.
        string? value = null;

        // Act
        var result = Result<string?>.Success(value);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
        result.Error.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_WhenValueTrueAndEmptyError_ReturnsSuccessWithValue()
    {
        // Arrange
        const int value = 7;

        // Act
        var result = new TestResult<int>(value, true, string.Empty);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(value);
        result.Error.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_WhenDefaultFalseAndError_ReturnsFailureWithError()
    {
        // Arrange
        const string error = "bad";

        // Act
        var result = new TestResult<int>(default!, false, error);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(error);
        result.Value.Should().Be(default(int));
    }

    [Fact]
    public void Constructor_WhenSuccessButHasError_ThrowsInvalidOperationException()
    {
        // Arrange
        // The base Result ctor invariant — success must not carry an error —
        // is enforced before Value is set.

        // Act
        var act = () => new TestResult<int>(42, true, "nope");

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Constructor_WhenFailureAndErrorIsEmpty_ThrowsInvalidOperationException()
    {
        // Arrange
        // A failure must carry a non-empty error.

        // Act
        var act = () => new TestResult<int>(default!, false, string.Empty);

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Constructor_WhenFailureAndErrorIsNull_ThrowsInvalidOperationException()
    {
        // Arrange
        // Null error on a failure violates the ctor guard.

        // Act
        var act = () => new TestResult<int>(default!, false, null!);

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ResultT_IsAssignableToResult()
    {
        // Arrange
        Result<int> generic = Result<int>.Success(1);

        // Act
        var asBase = (Result)generic;

        // Assert
        asBase.Should().NotBeNull();
        asBase.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Success_PreservesValueTupledType()
    {
        // Arrange
        var value = (Name: "abc", Count: 9);

        // Act
        var result = Result<(string Name, int Count)>.Success(value);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("abc");
        result.Value.Count.Should().Be(9);
    }
}
