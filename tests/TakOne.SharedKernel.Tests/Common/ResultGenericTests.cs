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
    public void Failure_WhenGivenError_ReturnsIsFailureTrueAndThrowsOnValueAccess()
    {
        // Arrange
        const string error = "kaboom";

        // Act
        var result = Result<int>.Failure(error);

        // Assert
        // SECURITY FIX (Brutal Code Review v3 #16): the Value getter now
        // throws on a failed Result — previously it silently returned
        // default(int) = 0, which let callers propagate 0 downstream
        // without noticing the failure. The throw makes the footgun loud.
        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);

        var act = () => result.Value;
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Cannot access Value of a failed Result*");
    }

    [Fact]
    public void Failure_WhenGivenErrorForReferenceType_ThrowsOnValueAccess()
    {
        // Arrange
        const string error = "missing";

        // Act
        var result = Result<string>.Failure(error);

        // Assert
        // SECURITY FIX (Brutal Code Review v3 #16): the Value getter now
        // throws on a failed Result — previously it silently returned
        // null for reference-type T, which let callers propagate null
        // downstream (NullReferenceException at an unpredictable call
        // site, far from the actual failure). The throw makes the footgun
        // loud and localizes the error to the misuse site.
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(error);

        var act = () => result.Value;
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Cannot access Value of a failed Result*");
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
    public void Constructor_WhenDefaultFalseAndError_ReturnsFailureWithErrorAndThrowsOnValueAccess()
    {
        // Arrange
        const string error = "bad";

        // Act
        var result = new TestResult<int>(default!, false, error);

        // Assert
        // SECURITY FIX (Brutal Code Review v3 #16): Value access on a
        // failed Result throws — even when constructed via the protected
        // internal ctor. This closes the footgun for ALL construction paths.
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(error);

        var act = () => result.Value;
        act.Should().Throw<InvalidOperationException>();
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
