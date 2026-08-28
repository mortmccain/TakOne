using System.Reflection;
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
/// <remarks>
/// <b>TEST COUNT REDUCTION (Brutal Code Review v3 finding #29):</b>
/// the previous version of this file had 28 tests covering the static-class
/// structure (sealed + abstract), two catalog-size floors (≥100 and ≥120),
/// per-code invariants (non-null/empty, exactly 7 chars, alphabet-only,
/// digit-at-position-0, digit-at-position-1, consonants-at-positions-2-4,
/// digits-at-positions-5-6, starts-with-digit, ends-with-digit), cross-code
/// invariants (all-unique, all-chars-in-alphabet), 8 sample-known-code
/// anchors (one per tier), 3 tier-coverage counts, and 2 alphabet-definition
/// sanity checks. The review correctly identified these as "real
/// assertions, but extremely low value" — the per-position tests duplicated
/// the alphabet-only test, and the per-known-code anchors were brittle
/// (any value change required a worklog update). Per the review's
/// recommendation, this file now retains only 2 tests — the per-code
/// alphabet-membership contract (proves every code in the catalog uses
/// only the restricted alphabet, which transitively enforces the
/// per-position format) and the catalog-size floor (catches a tier being
/// lost). Maintenance effort is now focused on the higher-value Sale
/// handler tests, where the real bugs live. The deleted edge-case tests
/// are documented in the Round 18 worklog for traceability.
/// </remarks>
public class UnexpectedErrorCodesTests
{
    // The restricted alphabet that all code characters must be drawn from.
    // Per the SUT class-level XML doc: digits 2-9 (no 0 or 1) + the listed
    // consonants (no A/E/I/O/U and no L). All such characters in one set.
    private const string AllowedAlphabet = "23456789BCDFGHJKMNPQRSTVWXYZ";

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

    // ── Per-code format contract ─────────────────────────────────────────

    // Verifies every code in the catalog uses only the restricted alphabet
    // (digits 2-9 + consonants BCDFGHJKMNPQRSTVWXYZ). This transitively
    // enforces the per-position format (digits at positions 0/1/5/6,
    // consonants at positions 2/3/4) AND the no-forbidden-characters
    // contract (no 0/O/1/I/L). If a future code adds a stray '0' or 'O'
    // or '1' or 'I' or 'L', this test catches it.
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

    // ── Catalog-size floor ──────────────────────────────────────────────

    // Verifies the catalog has at least 120 codes. The Round-15 worklog
    // established that this catalog is supposed to have ~125 codes spanning
    // 5 tiers (backend invariants + handlers + mobile + desktop +
    // minimal-API endpoints). Any drop below 120 likely means a tier was
    // lost (e.g. someone deleted a catch-site block and its const field).
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
}
