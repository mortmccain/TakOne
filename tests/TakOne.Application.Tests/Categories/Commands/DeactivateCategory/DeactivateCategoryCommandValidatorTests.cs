using System.Reflection;
using FluentAssertions;
using FluentValidation;
using TakOne.Application.Categories.Commands.DeactivateCategory;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Categories.Commands.DeactivateCategory;

/// <summary>
/// Unit tests for <see cref="DeactivateCategoryCommandValidator"/>.
///
/// SUT DISCOVERY (IMPORTANT): the SUT is an EMPTY STUB. The file
/// <c>TakOne.Application/Categories/Commands/DeactivateCategory/DeactivateCategoryCommandValidator.cs</c>
/// declares <c>internal class DeactivateCategoryCommandValidator { }</c> —
/// it does NOT inherit from <c>AbstractValidator&lt;T&gt;</c> and has NO
/// validation rules. This is inconsistent with all the other Category
/// command validators (Activate, Rename, Create) which are real
/// <c>AbstractValidator&lt;T&gt;</c> subclasses with NotEmpty rules.
///
/// The stub is declared <c>internal</c>, so this test project cannot
/// reference it directly (no <c>InternalsVisibleTo</c> attribute is set
/// on <c>TakOne.Application</c>). We use reflection to:
///   1. Load the type by name from the SUT assembly.
///   2. Assert its base type is <c>object</c> (NOT
///      <c>AbstractValidator&lt;DeactivateCategoryCommand&gt;</c>) —
///      locking in the current stub status.
///   3. Assert it has no public instance methods of its own — the
///      empty class body means the only inherited members come from
///      <c>object</c>.
///
/// This test file exists to DOCUMENT that the stub is currently empty
/// and to fail loudly if someone refactors the SUT to be a real
/// validator without also updating these tests (a refactor to a real
/// validator would be a welcome improvement, but the tests need to
/// change with it).
///
/// NOTE: the handler this validator nominally protects
/// (<see cref="DeactivateCategoryCommandHandler"/>) does its OWN
/// defense-in-depth checks (auth, not-found) — so the empty validator
/// is not currently a security hole. It IS a consistency gap with the
/// other Category command validators.
/// </summary>
public class DeactivateCategoryCommandValidatorTests
{
    // Load the SUT type by name from the Application assembly. The type
    // is internal, so we use the qualified namespace + name + non-public
    // binding flags.
    private static Type? LoadValidatorType()
    {
        var assembly = typeof(DeactivateCategoryCommand).Assembly;
        return assembly.GetType(
            "TakOne.Application.Categories.Commands.DeactivateCategory.DeactivateCategoryCommandValidator");
    }

    // ── Stub-status contract ───────────────────────────────────────────

    [Fact]
    public void ValidatorType_WhenLoadedFromApplicationAssembly_IsNotNull()
    {
        // Arrange
        // (no setup — pure reflection lookup)

        // Act
        var validatorType = LoadValidatorType();

        // Assert
        // The type exists in the SUT assembly — confirms the stub class
        // is present (a refactor that renamed or deleted the stub would
        // fail here).
        validatorType.Should().NotBeNull();
    }

    // The stub's base type is `object` — NOT AbstractValidator<T>.
    // This locks in the current stub status: if someone adds a real
    // AbstractValidator inheritance later, this test will fail and
    // force them to update the test suite to exercise the real rules.
    [Fact]
    public void ValidatorType_WhenInspected_DoesNotInheritFromAbstractValidator()
    {
        // Arrange
        var validatorType = LoadValidatorType();

        // Act
        var baseType = validatorType!.BaseType;

        // Assert
        baseType.Should().Be<object>();
    }

    // The stub has no public instance methods of its own (it's an empty
    // class body). If a future refactor adds a real Validate method, this
    // test fails — forcing the suite to be updated.
    [Fact]
    public void ValidatorType_WhenInspected_HasNoPublicInstanceMethodsOfItsOwn()
    {
        // Arrange
        var validatorType = LoadValidatorType();

        // Act
        // BindingFlags.Public | Instance | DeclaredOnly excludes
        // inherited object methods (ToString, Equals, GetHashCode,
        // GetType) — so this counts ONLY methods declared on the SUT
        // itself.
        var declaredMethods = validatorType!
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        // Assert
        // An empty class body has zero declared methods.
        declaredMethods.Should().BeEmpty();
    }
}
