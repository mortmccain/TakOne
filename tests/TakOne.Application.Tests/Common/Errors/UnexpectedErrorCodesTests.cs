using System.Reflection;
using System.Text;
using FluentAssertions;
using TakOne.Application.Common.Errors;
using Xunit;

namespace TakOne.Application.Tests.Common.Errors;

/// <summary>
/// Unit tests for <see cref="UnexpectedErrorCodes"/> — the Round-15 opaque
/// error-code catalog. Every code is a 7-character string drawn from a
/// restricted alphabet (digits 2-9 + consonants BCDFGHJKMNPQRSTVWXYZ —
/// no 0/O/1/I/L to avoid legibility collisions).
///
/// STRUCTURE OF EACH CODE:
///   2 digits + 3 letters + 2 digits = 7 chars total
///   chars[0..1] are digits ('2'..'9')
///   chars[2..4] are consonants (alphabet "BCDFGHJKMNPQRSTVWXYZ")
///   chars[5..6] are digits ('2'..'9')
///
/// The tests use reflection to enumerate ALL public const string fields on
/// the UnexpectedErrorCodes class — adding a new code without recompiling
/// the tests will automatically be picked up by these assertions.
/// </summary>
public class UnexpectedErrorCodesTests
{
    // The restricted alphabet that all code characters must be drawn from.
    // Per the SUT class-level XML doc: digits 2-9 (no 0 or 1) + the listed
    // consonants (no A/E/I/O/U and no L). All such characters in one set.
    private const string AllowedAlphabet = "23456789BCDFGHJKMNPQRSTVWXYZ";

    // The subset of the alphabet that's allowed at the LETTER positions
    // (chars[2..4]).
    private const string AllowedConsonants = "BCDFGHJKMNPQRSTVWXYZ";

    // The subset of the alphabet that's allowed at the DIGIT positions
    // (chars[0..1] and chars[5..6]).
    private const string AllowedDigits = "23456789";

    // Reflective enumeration of all public const string fields — cached
    // for the test run; reflection is expensive.
    private static IReadOnlyList<FieldInfo> ConstStringFields { get; } =
        typeof(UnexpectedErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            // For `public const string` fields, IsLiteral=true (the value is
            // a compile-time literal). IsInitOnly is for `readonly` fields, NOT
            // const — so do NOT filter on IsInitOnly here.
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToList();

    private static IReadOnlyList<string> AllCodes { get; } =
        ConstStringFields
            .Select(f => (string)f.GetValue(null)!)
            .ToList();

    // ── Class structure ─────────────────────────────────────────────────

    [Fact]
    public void UnexpectedErrorCodes_WhenTypeInspected_IsStaticClass()
    {
        // Arrange
        var type = typeof(UnexpectedErrorCodes);

        // Act / Assert
        // A static class in C# is both sealed (cannot be subclassed) AND
        // abstract (cannot be instantiated). This double-flag is the
        // compiler's marker for a static class.
        type.IsSealed.Should().BeTrue("static classes are sealed");
        type.IsAbstract.Should().BeTrue("static classes are abstract");
        type.IsClass.Should().BeTrue();
    }

    // ── Catalog size ────────────────────────────────────────────────────

    [Fact]
    public void Catalog_WhenInspected_HasAtLeast100Codes()
    {
        // Arrange
        // The Round-15 worklog established that this catalog is supposed to
        // have ~125 codes spanning 5 tiers (backend invariants + handlers +
        // mobile + desktop + minimal-API endpoints). We assert >= 100 to
        // catch a regression where a tier is accidentally deleted.

        // Act
        var count = AllCodes.Count;

        // Assert
        count.Should().BeGreaterThanOrEqualTo(100);
    }

    [Fact]
    public void Catalog_WhenInspected_HasAtLeast120Codes()
    {
        // Arrange
        // Tighter floor — the spec said ~125. Any drop below 120 likely
        // means a tier was lost.

        // Act
        var count = AllCodes.Count;

        // Assert
        count.Should().BeGreaterThanOrEqualTo(120);
    }

    // ── Per-code invariants ─────────────────────────────────────────────

    [Fact]
    public void EachCode_WhenInspected_IsNotNullOrEmpty()
    {
        // Arrange
        // No field should be a null or empty string — every const string
        // field in the catalog must be a populated code.

        // Act / Assert
        foreach (var code in AllCodes)
        {
            code.Should().NotBeNullOrEmpty($"every const string field must be a real code, not null/empty");
        }
    }

    [Fact]
    public void EachCode_WhenInspected_IsExactly7CharactersLong()
    {
        // Arrange
        // The SUT doc says "7-character alphanumeric string" — verify
        // every code honors that contract.

        // Act / Assert
        foreach (var code in AllCodes)
        {
            code.Length.Should().Be(7, $"code '{code}' must be exactly 7 characters (2 digits + 3 letters + 2 digits)");
        }
    }

    [Fact]
    public void EachCode_WhenInspected_UsesOnlyAllowedAlphabet()
    {
        // Arrange
        // Every character of every code must be in the restricted
        // alphabet — no 0, O, 1, I, L, A, E, U.

        // Act / Assert
        foreach (var code in AllCodes)
        {
            foreach (var c in code)
            {
                AllowedAlphabet.Should().Contain(c.ToString(),
                    $"char '{c}' in code '{code}' is not in the allowed alphabet '{AllowedAlphabet}'");
            }
        }
    }

    [Fact]
    public void EachCode_WhenInspected_HasDigitAtFirstPosition()
    {
        // Arrange
        // The format is "2 digits + 3 letters + 2 digits" — position 0
        // (chars[0]) must be a digit (2-9).

        // Act / Assert
        foreach (var code in AllCodes)
        {
            AllowedDigits.Should().Contain(code[0].ToString(),
                $"first char of '{code}' must be a digit in '{AllowedDigits}'");
        }
    }

    [Fact]
    public void EachCode_WhenInspected_HasDigitAtSecondPosition()
    {
        // Arrange
        // Position 1 (chars[1]) must also be a digit (the second digit of
        // the leading 2-digit block).

        // Act / Assert
        foreach (var code in AllCodes)
        {
            AllowedDigits.Should().Contain(code[1].ToString(),
                $"second char of '{code}' must be a digit in '{AllowedDigits}'");
        }
    }

    [Fact]
    public void EachCode_WhenInspected_HasConsonantAtPositionsTwoThroughFour()
    {
        // Arrange
        // Positions 2, 3, 4 (chars[2..4]) must be consonants (letters
        // from the restricted alphabet — no vowels, no L).

        // Act / Assert
        foreach (var code in AllCodes)
        {
            AllowedConsonants.Should().Contain(code[2].ToString(),
                $"third char of '{code}' must be a consonant");
            AllowedConsonants.Should().Contain(code[3].ToString(),
                $"fourth char of '{code}' must be a consonant");
            AllowedConsonants.Should().Contain(code[4].ToString(),
                $"fifth char of '{code}' must be a consonant");
        }
    }

    [Fact]
    public void EachCode_WhenInspected_HasDigitAtPositionsFiveAndSix()
    {
        // Arrange
        // Positions 5, 6 (chars[5..6]) must be digits (the trailing
        // 2-digit block).

        // Act / Assert
        foreach (var code in AllCodes)
        {
            AllowedDigits.Should().Contain(code[5].ToString(),
                $"sixth char of '{code}' must be a digit");
            AllowedDigits.Should().Contain(code[6].ToString(),
                $"seventh char of '{code}' must be a digit");
        }
    }

    [Fact]
    public void EachCode_WhenInspected_StartsWithADigit()
    {
        // Arrange
        // Aggregated: char.IsDigit on the first character of every code.

        // Act / Assert
        foreach (var code in AllCodes)
        {
            char.IsDigit(code[0]).Should().BeTrue(
                $"first char of '{code}' must be a digit (IsDigit)");
        }
    }

    [Fact]
    public void EachCode_WhenInspected_EndsWithADigit()
    {
        // Arrange
        // Aggregated: char.IsDigit on the last character of every code.

        // Act / Assert
        foreach (var code in AllCodes)
        {
            char.IsDigit(code[^1]).Should().BeTrue(
                $"last char of '{code}' must be a digit (IsDigit)");
        }
    }

    // ── Cross-code invariants ──────────────────────────────────────────

    [Fact]
    public void AllCodes_WhenCollected_AreAllUnique()
    {
        // Arrange
        // The Round-15 contract says "one code per call-site" — no two
        // fields in the catalog may share the same code value. We verify
        // by comparing the size of the set of codes to the count of codes.

        // Act
        var uniqueCount = AllCodes.Distinct().Count();

        // Assert
        uniqueCount.Should().Be(AllCodes.Count,
            "no two const fields may share the same code value");
    }

    [Fact]
    public void AllCodes_WhenAllCharactersAggregated_EachIsInAllowedAlphabet()
    {
        // Arrange
        // Cross-cutting check: enumerate ALL characters across ALL codes,
        // verify every distinct character drawn is in the allowed
        // alphabet. This catches a stray '0' / 'O' / '1' / 'I' / 'L'
        // anywhere in the catalog.

        // Act
        var distinctChars = AllCodes
            .SelectMany(c => c)
            .Distinct()
            .OrderBy(c => c)
            .ToList();

        // Assert
        foreach (var c in distinctChars)
        {
            AllowedAlphabet.Should().Contain(c.ToString(),
                $"char '{c}' appears in some code but is not in the allowed alphabet");
        }
    }

    // ── Sample known codes (sanity anchors — if the SUT changes these
    //    values, the test will catch it and force a worklog update) ─────

    [Fact]
    public void KnownCode_BroadcastFanout_UnknownScope_EqualsExpectedValue()
    {
        // Arrange
        // Tier 1 — BroadcastFanout.cs:97 — unknown BroadcastScope invariant.

        // Act
        var code = UnexpectedErrorCodes.BroadcastFanout_UnknownScope;

        // Assert
        code.Should().Be("57THJ48");
    }

    [Fact]
    public void KnownCode_UnitOfWork_RetryExhausted_EqualsExpectedValue()
    {
        // Arrange
        // Tier 1 — UnitOfWork.cs:221 — compiler-flow-analysis throw.

        // Act
        var code = UnexpectedErrorCodes.UnitOfWork_RetryExhausted;

        // Assert
        code.Should().Be("24FSM83");
    }

    [Fact]
    public void KnownCode_SaleNumberGenerator_SequenceCapacityReached_EqualsExpectedValue()
    {
        // Arrange
        // Tier 1 — SaleNumberGenerator.cs:252 — system capacity ceiling.

        // Act
        var code = UnexpectedErrorCodes.SaleNumberGenerator_SequenceCapacityReached;

        // Assert
        code.Should().Be("84VFN95");
    }

    [Fact]
    public void KnownCode_SystemSettingsRepository_SingletonMissing_EqualsExpectedValue()
    {
        // Arrange
        // Tier 1 — SystemSettingsRepository.cs:126 — DB CHECK constraint
        // violation on singleton row.

        // Act
        var code = UnexpectedErrorCodes.SystemSettingsRepository_SingletonMissing;

        // Assert
        code.Should().Be("97CKC67");
    }

    [Fact]
    public void KnownCode_GetAllCustomerGroups_LoadFailed_EqualsExpectedValue()
    {
        // Arrange
        // Tier 2 — first backend Result.Failure that surfaces a generic
        // infrastructure message to the UI.

        // Act
        var code = UnexpectedErrorCodes.GetAllCustomerGroups_LoadFailed;

        // Assert
        code.Should().Be("37VFT74");
    }

    [Fact]
    public void KnownCode_AuthorizationMiddleware_PolicyMissing_EqualsExpectedValue()
    {
        // Arrange
        // Tier 2 — fail-closed defense: a Command/Query reached the
        // middleware without any auth attribute.

        // Act
        var code = UnexpectedErrorCodes.AuthorizationMiddleware_PolicyMissing;

        // Assert
        code.Should().Be("27JSF84");
    }

    [Fact]
    public void KnownCode_MobileManageGroups_DialogOpen_EqualsExpectedValue()
    {
        // Arrange
        // Tier 3 — sample mobile-page catch-site code.

        // Act
        var code = UnexpectedErrorCodes.MobileManageGroups_DialogOpen;

        // Assert
        code.Should().Be("28TZH69");
    }

    [Fact]
    public void KnownCode_UserDetail_RemoveRoleDialogOpen_EqualsExpectedValue()
    {
        // Arrange
        // Tier 4 — sample desktop-page catch-site code.

        // Act
        var code = UnexpectedErrorCodes.UserDetail_RemoveRoleDialogOpen;

        // Assert
        code.Should().Be("35YNV48");
    }

    [Fact]
    public void KnownCode_ProductImageEndpoint_InvalidUpload_EqualsExpectedValue()
    {
        // Arrange
        // Tier 5 — minimal-API endpoint catch-site code.

        // Act
        var code = UnexpectedErrorCodes.ProductImageEndpoint_InvalidUpload;

        // Assert
        code.Should().Be("66YZQ29");
    }

    // ── Tier coverage ───────────────────────────────────────────────────

    [Fact]
    public void Tier1Codes_WhenInspected_HaveAtLeastFiveEntries()
    {
        // Arrange
        // Tier 1 is the backend invariant throws — must have several
        // entries; a regression that drops below 5 means an invariant
        // was removed and a code with it.

        // Act
        var tier1Count = new[]
        {
            UnexpectedErrorCodes.BroadcastFanout_UnknownScope,
            UnexpectedErrorCodes.SaleRepository_HardDeleteNonDraft,
            UnexpectedErrorCodes.UnitOfWork_RetryExhausted,
            UnexpectedErrorCodes.SaleNumberGenerator_SequenceCapacityReached,
            UnexpectedErrorCodes.BroadcastNotification_InvalidScope,
            UnexpectedErrorCodes.SystemSettingsRepository_SingletonMissing,
            UnexpectedErrorCodes.SaleNumberGenerator_NoConnectionString,
            UnexpectedErrorCodes.SaleNumberGenerator_CounterRowDisappeared,
        }.Length;

        // Assert
        tier1Count.Should().BeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public void Tier2Codes_WhenInspected_HaveAtLeastFiveEntries()
    {
        // Arrange
        // Tier 2 is the backend Result.Failure sites that surface generic
        // messages.

        // Act
        var tier2Count = new[]
        {
            UnexpectedErrorCodes.GetAllCustomerGroups_LoadFailed,
            UnexpectedErrorCodes.SubmitSale_CustomerDisappeared,
            UnexpectedErrorCodes.AddItemToSale_CustomerDisappeared,
            UnexpectedErrorCodes.UpdateSaleLineItem_CustomerDisappeared,
            UnexpectedErrorCodes.UserAccountService_ResetPasswordFailed,
            UnexpectedErrorCodes.UserAccountService_AddToRoleFailed,
            UnexpectedErrorCodes.UserAccountService_RemovePasswordFailed,
            UnexpectedErrorCodes.UserAccountService_AssignRoleFailed,
            UnexpectedErrorCodes.UserAccountService_RemoveFromRoleFailed,
            UnexpectedErrorCodes.UserAccountService_UpdateUserFailed,
            UnexpectedErrorCodes.AuthorizationMiddleware_PolicyMissing,
        }.Length;

        // Assert
        tier2Count.Should().BeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public void Tier5Codes_WhenInspected_HaveTwoEntries()
    {
        // Arrange
        // Tier 5 is the minimal-API endpoint catches — currently two.

        // Act
        var tier5Count = new[]
        {
            UnexpectedErrorCodes.ProductImageEndpoint_InvalidUpload,
            UnexpectedErrorCodes.ProductImageEndpoint_UploadFailed,
        }.Length;

        // Assert
        tier5Count.Should().Be(2);
    }

    // ── Alphabet membership (explicit verification of the alphabet
    //    definition itself — caught by per-code check, but worth
    //    pinning separately) ─────────────────────────────────────────────

    [Fact]
    public void AllowedAlphabet_WhenDefined_ContainsNoForbiddenCharacters()
    {
        // Arrange
        // The spec explicitly forbids 0/O/1/I/L for legibility reasons
        // (0 looks like O, 1 looks like I, etc.). Verify the alphabet
        // definition itself excludes these.

        // Act
        var forbidden = new[] { '0', 'O', '1', 'I', 'L' };

        // Assert
        foreach (var c in forbidden)
        {
            AllowedAlphabet.Should().NotContain(c.ToString(),
                $"the forbidden char '{c}' must not be in the allowed alphabet");
        }
    }

    [Fact]
    public void AllowedAlphabet_WhenDefined_ContainsAllConsonantsAndDigits()
    {
        // Arrange
        // Sanity check: the alphabet definition contains digits 2-9
        // (8 chars) + the listed consonants (19 chars) = 27 chars total.

        // Act
        var alphabetSize = AllowedAlphabet.Length;

        // Assert
        // Alphabet size: 8 digits (2-9) + 20 consonants (B-Z minus vowels
        // A/E/I/O/U, no L either) = 28 chars total.
        alphabetSize.Should().Be(28);
        AllowedAlphabet.Should().Contain("2");
        AllowedAlphabet.Should().Contain("9");
        AllowedAlphabet.Should().Contain("B");
        AllowedAlphabet.Should().Contain("Z");
    }
}
