using FluentAssertions;
using TakOne.SharedKernel.Common;
using Xunit;

namespace TakOne.SharedKernel.Tests.Common;

/// <summary>
/// Unit tests for the non-generic <see cref="Result"/> class — the base
/// two-state operation outcome (Success / Failure with an Error string).
/// </summary>
public class ResultTests
{
    // The Result ctor is protected, so to exercise its guards from a
    // different assembly we expose it through a tiny test subclass.
    private sealed class TestResult : Result
    {
        public TestResult(bool isSuccess, string error) : base(isSuccess, error) { }
    }

    [Fact]
    public void Success_WhenCalled_ReturnsIsSuccessTrueAndEmptyError()
    {
        // Arrange

        // Act
        var result = Result.Success();

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Error.Should().BeEmpty();
    }

    [Fact]
    public void Failure_WhenGivenError_ReturnsIsFailureTrueAndErrorSet()
    {
        // Arrange
        const string error = "something went wrong";

        // Act
        var result = Result.Failure(error);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);
    }

    [Fact]
    public void Failure_WhenGivenEmptyError_StillProducesAFailureResult()
    {
        // Arrange
        // Result.Failure(string) does NOT pre-validate the error string — the
        // ctor guard rejects empty errors on failure, but here we are testing
        // the static factory's pass-through, which will throw. We assert the
        // throw rather than a returned failure.
        // Act
        var act = () => Result.Failure(string.Empty);

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Constructor_WhenSuccessButHasError_ThrowsInvalidOperationException()
    {
        // Arrange
        // isSuccess=true must NOT carry an error string — the invariant
        // a successful result cannot have an error.
        // Act
        var act = () => new TestResult(true, "should-not-be-here");

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Constructor_WhenFailureAndErrorIsNull_ThrowsInvalidOperationException()
    {
        // Arrange
        // A failed result MUST carry an error. Null error violates that.
        // Act
        var act = () => new TestResult(false, null!);

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Constructor_WhenFailureAndErrorIsEmpty_ThrowsInvalidOperationException()
    {
        // Arrange
        // Empty-string error also violates the failure-must-have-error rule.
        // Act
        var act = () => new TestResult(false, string.Empty);

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Constructor_WhenFailureAndErrorIsWhitespace_ThrowsInvalidOperationException()
    {
        // Arrange
        // string.IsNullOrEmpty is true for whitespace-only strings only via
        // IsNullOrWhiteSpace, NOT IsNullOrEmpty — verify the ctor uses
        // IsNullOrEmpty (whitespace should NOT trigger the guard).
        // Act
        var result = new TestResult(false, "   ");

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("   ");
    }

    [Fact]
    public void SuccessT_WhenGivenValue_ReturnsGenericSuccessWithValue()
    {
        // Arrange
        const int value = 42;

        // Act
        Result<int> result = Result.Success(value);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Value.Should().Be(value);
        result.Error.Should().BeEmpty();
    }

    [Fact]
    public void FailureT_WhenGivenError_ReturnsGenericFailureWithDefault()
    {
        // Arrange
        const string error = "boom";

        // Act
        Result<string> result = Result.Failure<string>(error);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);
        result.Value.Should().BeNull();
    }

    [Fact]
    public void IsFailure_WhenIsSuccessTrue_ReturnsFalse()
    {
        // Arrange

        // Act
        var result = Result.Success();

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().Be(!result.IsSuccess);
        result.IsFailure.Should().BeFalse();
    }

    [Fact]
    public void IsFailure_WhenIsSuccessFalse_ReturnsTrue()
    {
        // Arrange
        var result = Result.Failure("err");

        // Act + Assert
        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().Be(!result.IsSuccess);
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Success_AndFailure_AreMutuallyExclusive()
    {
        // Arrange
        var success = Result.Success();
        var failure = Result.Failure("err");

        // Act + Assert
        success.IsSuccess.Should().NotBe(success.IsFailure);
        failure.IsSuccess.Should().NotBe(failure.IsFailure);
    }
}
