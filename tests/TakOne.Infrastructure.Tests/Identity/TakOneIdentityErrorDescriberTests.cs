using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;
using NSubstitute;
using TakOne.Infrastructure.Identity;
using TakOne.Infrastructure.Localization;
using Xunit;

namespace TakOne.Infrastructure.Tests.Identity;

/// <summary>
/// Unit tests for <see cref="TakOneIdentityErrorDescriber"/>.
///
/// COVERAGE APPROACH:
///   The describer is a thin localized wrapper over the ASP.NET Identity
///   base class <see cref="IdentityErrorDescriber"/>. Each override
///   returns an <see cref="IdentityError"/> whose Code is the method name
///   (so log greps and error-code switches keep working) and whose
///   Description is sourced from <c>IStringLocalizer&lt;IdentityErrorMessages&gt;</c>
///   — either directly (no-parameter methods) or via <see cref="string.Format(string,object)"/>
///   (single-argument methods where the resx value contains a <c>{0}</c>
///   placeholder for the argument).
///
///   Each test mocks the localizer with a known return value per key and
///   asserts on the resulting IdentityError's Code AND Description. The
///   "localizer.Received()[key]" pattern is used in two dedicated tests
///   to verify the call-site actually hit the localizer (behavior + interaction
///   verification).
///
/// SUT LOCATION:
///   TakOne.Infrastructure/Identity/TakOneIdentityErrorDescriber.cs
/// </summary>
public class TakOneIdentityErrorDescriberTests
{
    // ── Helpers ───────────────────────────────────────────────────────

    // Builds a mock IStringLocalizer<IdentityErrorMessages> with known
    // return values for EVERY key the describer consults. Each test can
    // override individual keys if it wants to assert a specific value.
    //
    // The resx-style format strings use {0} placeholders for the methods
    // that pass an argument through string.Format. The non-format methods
    // just return plain strings.
    private static IStringLocalizer<IdentityErrorMessages> BuildLocalizer()
    {
        var localizer = Substitute.For<IStringLocalizer<IdentityErrorMessages>>();
        localizer["PasswordTooShort"].Returns(new LocalizedString("PasswordTooShort", "Password must be at least {0} characters."));
        localizer["PasswordRequiresNonAlphanumeric"].Returns(new LocalizedString("PasswordRequiresNonAlphanumeric", "Passwords must have at least one non-alphanumeric character."));
        localizer["PasswordRequiresDigit"].Returns(new LocalizedString("PasswordRequiresDigit", "Passwords must have at least one digit ('0'-'9')."));
        localizer["PasswordRequiresUpper"].Returns(new LocalizedString("PasswordRequiresUpper", "Passwords must have at least one uppercase ('A'-'Z')."));
        localizer["PasswordRequiresLower"].Returns(new LocalizedString("PasswordRequiresLower", "Passwords must have at least one lowercase ('a'-'z')."));
        localizer["PasswordRequiresUniqueChars"].Returns(new LocalizedString("PasswordRequiresUniqueChars", "Passwords must use at least {0} distinct characters."));
        localizer["DuplicateUserName"].Returns(new LocalizedString("DuplicateUserName", "Worker ID '{0}' is already taken."));
        localizer["DuplicateEmail"].Returns(new LocalizedString("DuplicateEmail", "Email '{0}' is already registered."));
        localizer["InvalidUserName"].Returns(new LocalizedString("InvalidUserName", "Worker ID '{0}' is invalid."));
        localizer["InvalidEmail"].Returns(new LocalizedString("InvalidEmail", "Email '{0}' is not a valid address."));
        localizer["InvalidToken"].Returns(new LocalizedString("InvalidToken", "The security code is invalid or has expired."));
        localizer["PasswordMismatch"].Returns(new LocalizedString("PasswordMismatch", "The new password and confirmation do not match."));
        localizer["DefaultError"].Returns(new LocalizedString("DefaultError", "An unexpected error occurred."));
        return localizer;
    }

    // ── Type-level contract tests ─────────────────────────────────────

    [Fact]
    public void Class_IsSealed_True()
    {
        // Arrange / Act
        // Assert — the describer is sealed because the project registers a
        // single concrete class via AddErrorDescriber<T>(); subclasses are
        // not part of the extensibility surface.
        typeof(TakOneIdentityErrorDescriber).IsSealed.Should().BeTrue();
    }

    [Fact]
    public void Class_IsAssignableTo_IdentityErrorDescriber()
    {
        // Arrange / Act / Assert — registered with Identity via the
        // AddErrorDescriber<T> flavor that requires a subclass of
        // IdentityErrorDescriber.
        typeof(TakOneIdentityErrorDescriber).IsAssignableTo(typeof(IdentityErrorDescriber)).Should().BeTrue();
    }

    [Fact]
    public void Constructor_TakesSingleIStringLocalizerParameter()
    {
        // Arrange / Act
        var ctors = typeof(TakOneIdentityErrorDescriber).GetConstructors();

        // Assert — exactly one public constructor, with exactly one
        // parameter of type IStringLocalizer<IdentityErrorMessages>.
        ctors.Should().HaveCount(1);
        var only = ctors[0];
        var args = only.GetParameters();
        args.Should().HaveCount(1);
        args[0].ParameterType.Should().Be(typeof(IStringLocalizer<IdentityErrorMessages>));
    }

    // ── Password complexity overrides ─────────────────────────────────

    [Fact]
    public void PasswordTooShort_ReturnsCodeAndLocalizedDescriptionWithLengthSubstituted()
    {
        // Arrange
        var localizer = BuildLocalizer();
        var sut = new TakOneIdentityErrorDescriber(localizer);

        // Act
        var error = sut.PasswordTooShort(8);

        // Assert — Code is the method name (matches Identity's base
        // describer's contract, so log greps work unchanged).
        error.Code.Should().Be("PasswordTooShort");
        // Description has the {0} placeholder substituted with the length.
        error.Description.Should().Contain("8");
        error.Description.Should().NotContain("{0}");
        error.Description.Should().Be("Password must be at least 8 characters.");
    }

    [Fact]
    public void PasswordTooShort_WithLargeLength_SubstitutesCorrectly()
    {
        // Arrange — boundary check: a 3-digit length should substitute cleanly.
        var localizer = BuildLocalizer();
        var sut = new TakOneIdentityErrorDescriber(localizer);

        // Act
        var error = sut.PasswordTooShort(128);

        // Assert
        error.Code.Should().Be("PasswordTooShort");
        error.Description.Should().Contain("128");
        error.Description.Should().Be("Password must be at least 128 characters.");
    }

    [Fact]
    public void PasswordRequiresNonAlphanumeric_ReturnsCodeAndLocalizedDescription()
    {
        // Arrange
        var localizer = BuildLocalizer();
        var sut = new TakOneIdentityErrorDescriber(localizer);

        // Act
        var error = sut.PasswordRequiresNonAlphanumeric();

        // Assert — no parameter to substitute; Description is the raw resx value.
        error.Code.Should().Be("PasswordRequiresNonAlphanumeric");
        error.Description.Should().Be("Passwords must have at least one non-alphanumeric character.");
    }

    [Fact]
    public void PasswordRequiresDigit_ReturnsCodeAndLocalizedDescription()
    {
        // Arrange
        var localizer = BuildLocalizer();
        var sut = new TakOneIdentityErrorDescriber(localizer);

        // Act
        var error = sut.PasswordRequiresDigit();

        // Assert
        error.Code.Should().Be("PasswordRequiresDigit");
        error.Description.Should().Be("Passwords must have at least one digit ('0'-'9').");
    }

    [Fact]
    public void PasswordRequiresUpper_ReturnsCodeAndLocalizedDescription()
    {
        // Arrange
        var localizer = BuildLocalizer();
        var sut = new TakOneIdentityErrorDescriber(localizer);

        // Act
        var error = sut.PasswordRequiresUpper();

        // Assert
        error.Code.Should().Be("PasswordRequiresUpper");
        error.Description.Should().Be("Passwords must have at least one uppercase ('A'-'Z').");
    }

    [Fact]
    public void PasswordRequiresLower_ReturnsCodeAndLocalizedDescription()
    {
        // Arrange
        var localizer = BuildLocalizer();
        var sut = new TakOneIdentityErrorDescriber(localizer);

        // Act
        var error = sut.PasswordRequiresLower();

        // Assert
        error.Code.Should().Be("PasswordRequiresLower");
        error.Description.Should().Be("Passwords must have at least one lowercase ('a'-'z').");
    }

    [Fact]
    public void PasswordRequiresUniqueChars_ReturnsCodeAndDescriptionWithUniqueCharsSubstituted()
    {
        // Arrange
        var localizer = BuildLocalizer();
        var sut = new TakOneIdentityErrorDescriber(localizer);

        // Act
        var error = sut.PasswordRequiresUniqueChars(8);

        // Assert
        error.Code.Should().Be("PasswordRequiresUniqueChars");
        error.Description.Should().Contain("8");
        error.Description.Should().NotContain("{0}");
        error.Description.Should().Be("Passwords must use at least 8 distinct characters.");
    }

    // ── Duplicate user / email overrides ─────────────────────────────

    [Fact]
    public void DuplicateUserName_ReturnsCodeAndDescriptionWithUserNameSubstituted()
    {
        // Arrange
        var localizer = BuildLocalizer();
        var sut = new TakOneIdentityErrorDescriber(localizer);

        // Act
        var error = sut.DuplicateUserName("alice");

        // Assert
        error.Code.Should().Be("DuplicateUserName");
        error.Description.Should().Contain("alice");
        error.Description.Should().NotContain("{0}");
        error.Description.Should().Be("Worker ID 'alice' is already taken.");
    }

    [Fact]
    public void DuplicateUserName_WithPersianName_SubstitutesCorrectly()
    {
        // Arrange — Persian text must survive string.Format unchanged
        // (it has no braces, so it should not be misinterpreted as a format spec).
        var localizer = BuildLocalizer();
        var sut = new TakOneIdentityErrorDescriber(localizer);

        // Act
        var error = sut.DuplicateUserName("علی");

        // Assert
        error.Code.Should().Be("DuplicateUserName");
        error.Description.Should().Contain("علی");
    }

    [Fact]
    public void DuplicateEmail_ReturnsCodeAndDescriptionWithEmailSubstituted()
    {
        // Arrange
        var localizer = BuildLocalizer();
        var sut = new TakOneIdentityErrorDescriber(localizer);

        // Act
        var error = sut.DuplicateEmail("alice@example.com");

        // Assert
        error.Code.Should().Be("DuplicateEmail");
        error.Description.Should().Contain("alice@example.com");
        error.Description.Should().Be("Email 'alice@example.com' is already registered.");
    }

    // ── Invalid user / email format overrides (nullable arg variants) ──

    [Fact]
    public void InvalidUserName_WithValue_ReturnsCodeAndDescriptionWithUserNameSubstituted()
    {
        // Arrange
        var localizer = BuildLocalizer();
        var sut = new TakOneIdentityErrorDescriber(localizer);

        // Act
        var error = sut.InvalidUserName("bob");

        // Assert
        error.Code.Should().Be("InvalidUserName");
        error.Description.Should().Contain("bob");
        error.Description.Should().Be("Worker ID 'bob' is invalid.");
    }

    [Fact]
    public void InvalidUserName_WithNull_DoesNotThrowAndCodeIsCorrect()
    {
        // Arrange — string.Format with a null arg substitutes empty string
        // for the {0} placeholder, so the resulting Description is the
        // format string with the placeholder removed. This verifies no
        // NullReferenceException is thrown when Identity passes null.
        var localizer = BuildLocalizer();
        var sut = new TakOneIdentityErrorDescriber(localizer);

        // Act
        var act = () => sut.InvalidUserName(null!);

        // Assert
        var error = act.Should().NotThrow().Subject;
        error.Code.Should().Be("InvalidUserName");
        error.Description.Should().NotContain("{0}");
    }

    [Fact]
    public void InvalidEmail_WithValue_ReturnsCodeAndDescriptionWithEmailSubstituted()
    {
        // Arrange
        var localizer = BuildLocalizer();
        var sut = new TakOneIdentityErrorDescriber(localizer);

        // Act
        var error = sut.InvalidEmail("not-an-email");

        // Assert
        error.Code.Should().Be("InvalidEmail");
        error.Description.Should().Contain("not-an-email");
        error.Description.Should().Be("Email 'not-an-email' is not a valid address.");
    }

    [Fact]
    public void InvalidEmail_WithNull_DoesNotThrowAndCodeIsCorrect()
    {
        // Arrange — same null-arg tolerance as InvalidUserName.
        var localizer = BuildLocalizer();
        var sut = new TakOneIdentityErrorDescriber(localizer);

        // Act
        var act = () => sut.InvalidEmail(null!);

        // Assert
        var error = act.Should().NotThrow().Subject;
        error.Code.Should().Be("InvalidEmail");
        error.Description.Should().NotContain("{0}");
    }

    // ── Token / password reset overrides (no argument) ───────────────

    [Fact]
    public void InvalidToken_ReturnsCodeAndLocalizedDescription()
    {
        // Arrange
        var localizer = BuildLocalizer();
        var sut = new TakOneIdentityErrorDescriber(localizer);

        // Act
        var error = sut.InvalidToken();

        // Assert
        error.Code.Should().Be("InvalidToken");
        error.Description.Should().Be("The security code is invalid or has expired.");
    }

    [Fact]
    public void PasswordMismatch_ReturnsCodeAndLocalizedDescription()
    {
        // Arrange
        var localizer = BuildLocalizer();
        var sut = new TakOneIdentityErrorDescriber(localizer);

        // Act
        var error = sut.PasswordMismatch();

        // Assert
        error.Code.Should().Be("PasswordMismatch");
        error.Description.Should().Be("The new password and confirmation do not match.");
    }

    [Fact]
    public void DefaultError_ReturnsCodeAndLocalizedDescription()
    {
        // Arrange
        var localizer = BuildLocalizer();
        var sut = new TakOneIdentityErrorDescriber(localizer);

        // Act
        var error = sut.DefaultError();

        // Assert
        error.Code.Should().Be("DefaultError");
        error.Description.Should().Be("An unexpected error occurred.");
    }

    // ── Localizer interaction verification ────────────────────────────

    [Fact]
    public void PasswordTooShort_CallsLocalizerWithPasswordTooShortKey()
    {
        // Arrange — fresh localizer with the standard mock values.
        var localizer = BuildLocalizer();
        var sut = new TakOneIdentityErrorDescriber(localizer);

        // Act
        _ = sut.PasswordTooShort(8);

        // Assert — NSubstitute's Received() syntax for indexers: accessing
        // the indexer on localizer.Received() asserts that at least one
        // call was made with that exact key. Discard the return value.
        _ = localizer.Received()["PasswordTooShort"];
    }

    [Fact]
    public void DefaultError_CallsLocalizerWithDefaultErrorKey()
    {
        // Arrange
        var localizer = BuildLocalizer();
        var sut = new TakOneIdentityErrorDescriber(localizer);

        // Act
        _ = sut.DefaultError();

        // Assert
        _ = localizer.Received()["DefaultError"];
    }

    [Fact]
    public void InvalidToken_CallsLocalizerWithInvalidTokenKey()
    {
        // Arrange
        var localizer = BuildLocalizer();
        var sut = new TakOneIdentityErrorDescriber(localizer);

        // Act
        _ = sut.InvalidToken();

        // Assert
        _ = localizer.Received()["InvalidToken"];
    }

    // ── Cross-cutting Code uniqueness ─────────────────────────────────

    [Fact]
    public void AllOverrides_ReturnDistinctCodes()
    {
        // Arrange — every override's Code is the method name. Sanity check
        // that no two overrides accidentally share a Code (which would
        // collapse Identity's error-code switches downstream).
        var localizer = BuildLocalizer();
        var sut = new TakOneIdentityErrorDescriber(localizer);

        // Act
        var codes = new[]
        {
            sut.PasswordTooShort(1).Code,
            sut.PasswordRequiresNonAlphanumeric().Code,
            sut.PasswordRequiresDigit().Code,
            sut.PasswordRequiresUpper().Code,
            sut.PasswordRequiresLower().Code,
            sut.PasswordRequiresUniqueChars(1).Code,
            sut.DuplicateUserName("a").Code,
            sut.DuplicateEmail("a@b.com").Code,
            sut.InvalidUserName("a").Code,
            sut.InvalidEmail("a").Code,
            sut.InvalidToken().Code,
            sut.PasswordMismatch().Code,
            sut.DefaultError().Code,
        };

        // Assert — all 13 Codes are distinct.
        codes.Should().OnlyHaveUniqueItems();
        codes.Should().HaveCount(13);
    }

    // ── Description-vs-Code coupling ─────────────────────────────────

    [Fact]
    public void PasswordRequiresNonAlphanumeric_DescriptionEqualsLocalizerValueExactly()
    {
        // Arrange — the no-arg overrides pass the LocalizedString.Value
        // straight through; they do NOT do any string.Format on it.
        // Verify that a resx value with no {0} placeholder is preserved
        // verbatim (i.e., the describer doesn't wrap it in any prefix/suffix).
        var localizer = Substitute.For<IStringLocalizer<IdentityErrorMessages>>();
        localizer["PasswordRequiresNonAlphanumeric"]
            .Returns(new LocalizedString("PasswordRequiresNonAlphanumeric", "abcXYZ123"));
        var sut = new TakOneIdentityErrorDescriber(localizer);

        // Act
        var error = sut.PasswordRequiresNonAlphanumeric();

        // Assert — Description is exactly the LocalizedString's Value.
        error.Description.Should().Be("abcXYZ123");
    }

    [Fact]
    public void PasswordTooShort_DescriptionUsesStringFormatWithLength()
    {
        // Arrange — string.Format("Hello {0}!", 7) → "Hello 7!"
        // The SUT uses string.Format(localizer["PasswordTooShort"], length),
        // so the {0} placeholder is replaced by the length's ToString().
        var localizer = Substitute.For<IStringLocalizer<IdentityErrorMessages>>();
        localizer["PasswordTooShort"].Returns(new LocalizedString("PasswordTooShort", "Need {0}!"));
        var sut = new TakOneIdentityErrorDescriber(localizer);

        // Act
        var error = sut.PasswordTooShort(7);

        // Assert
        error.Description.Should().Be("Need 7!");
    }

    [Fact]
    public void PasswordRequiresUniqueChars_DescriptionUsesStringFormatWithUniqueChars()
    {
        // Arrange — same string.Format pattern as PasswordTooShort.
        var localizer = Substitute.For<IStringLocalizer<IdentityErrorMessages>>();
        localizer["PasswordRequiresUniqueChars"].Returns(new LocalizedString("PasswordRequiresUniqueChars", "need {0} distinct"));
        var sut = new TakOneIdentityErrorDescriber(localizer);

        // Act
        var error = sut.PasswordRequiresUniqueChars(4);

        // Assert
        error.Description.Should().Be("need 4 distinct");
    }
}
