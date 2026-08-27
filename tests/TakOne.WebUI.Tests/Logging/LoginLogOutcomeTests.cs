using System.Reflection;
using FluentAssertions;
using TakOne.WebUI.Services.Logging;
using Xunit;

namespace TakOne.WebUI.Tests.Logging;

/// <summary>
/// Unit tests for <see cref="LoginLogOutcome"/> — the coarse-grained
/// outcome enum logged by <see cref="LoginAuditLogger"/> in any
/// environment.
/// </summary>
/// <remarks>
/// <para>
/// <b>SUT scope.</b> An <c>enum</c> (implicitly derives from
/// <see cref="Enum"/>) with exactly 5 named values:
/// <see cref="LoginLogOutcome.InvalidCredentials"/> = 0,
/// <see cref="LoginLogOutcome.LockedOut"/> = 1,
/// <see cref="LoginLogOutcome.Success"/> = 2,
/// <see cref="LoginLogOutcome.Exception"/> = 3,
/// <see cref="LoginLogOutcome.NoHttpContext"/> = 4.
/// </para>
/// <para>
/// <b>Security invariant.</b> The enum is INTENTIONALLY coarse-grained:
/// multiple Identity outcomes (Failed, IsNotAllowed, RequiresTwoFactor,
/// user-not-found, IsActive=false) collapse into the single
/// <see cref="LoginLogOutcome.InvalidCredentials"/> bucket so an attacker
/// with log access can't tell which credential probes resolve to valid
/// user accounts. The enum value list is itself the contract — adding a
/// new value requires an SUT change AND a LoginAuditLogger.Log switch
/// update. Tests assert the value count stays exactly 5 so future
/// additions surface as a CI break here first.
/// </para>
/// </remarks>
public class LoginLogOutcomeTests
{
    // ───────────────────────────────────────────────────────────────────────
    // Enum member count (the security invariant)
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Enum_HasExactlyFiveValues()
    {
        // Arrange / Act
        var values = Enum.GetValues<LoginLogOutcome>();

        // Assert — the security invariant: exactly 5 outcome buckets.
        // If this test ever fails, the security review checklist MUST be
        // re-consulted before merging the change.
        values.Should().HaveCount(5);
    }

    [Fact]
    public void EnumNames_AreInDeclarationOrder()
    {
        // Arrange / Act
        var names = Enum.GetNames<LoginLogOutcome>();

        // Assert — the declaration order is part of the SUT's audit contract;
        // if a value is ever inserted in the middle, the names array will
        // reorder and this test will catch it.
        names.Should().ContainInOrder(
            nameof(LoginLogOutcome.InvalidCredentials),
            nameof(LoginLogOutcome.LockedOut),
            nameof(LoginLogOutcome.Success),
            nameof(LoginLogOutcome.Exception),
            nameof(LoginLogOutcome.NoHttpContext));
    }

    // ───────────────────────────────────────────────────────────────────────
    // Explicit numeric values
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void InvalidCredentials_HasValueZero()
    {
        // Arrange / Act
        var value = (int)LoginLogOutcome.InvalidCredentials;

        // Assert — InvalidCredentials is the default value (0), so a
        // default(LoginLogOutcome) is InvalidCredentials — this is the
        // safest possible default (treat unknown as failure).
        value.Should().Be(0);
    }

    [Fact]
    public void LockedOut_HasValueOne()
    {
        // Arrange / Act
        var value = (int)LoginLogOutcome.LockedOut;

        // Assert — LockedOut is a distinct value so SIEM can correlate
        // brute-force attacks (vs InvalidCredentials which collapses
        // multiple Identity states).
        value.Should().Be(1);
    }

    [Fact]
    public void Success_HasValueTwo()
    {
        // Arrange / Act
        var value = (int)LoginLogOutcome.Success;

        // Assert
        value.Should().Be(2);
    }

    [Fact]
    public void Exception_HasValueThree()
    {
        // Arrange / Act
        var value = (int)LoginLogOutcome.Exception;

        // Assert
        value.Should().Be(3);
    }

    [Fact]
    public void NoHttpContext_HasValueFour()
    {
        // Arrange / Act
        var value = (int)LoginLogOutcome.NoHttpContext;

        // Assert
        value.Should().Be(4);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Enum.IsDefined behavior (defense-in-depth)
    // ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(LoginLogOutcome.InvalidCredentials)]
    [InlineData(LoginLogOutcome.LockedOut)]
    [InlineData(LoginLogOutcome.Success)]
    [InlineData(LoginLogOutcome.Exception)]
    [InlineData(LoginLogOutcome.NoHttpContext)]
    public void IsDefined_ForEveryDeclaredValue_ReturnsTrue(LoginLogOutcome outcome)
    {
        // Arrange / Act
        var isDefined = Enum.IsDefined(outcome);

        // Assert — every declared value passes IsDefined
        isDefined.Should().BeTrue();
    }

    [Fact]
    public void IsDefined_ForUndeclaredValue_ReturnsFalse()
    {
        // Arrange — 999 is not declared in the enum
        const LoginLogOutcome unknown = (LoginLogOutcome)999;

        // Act
        var isDefined = Enum.IsDefined(unknown);

        // Assert — undeclared values are rejected by IsDefined; this is
        // the contract LoginAuditLogger.Log's default branch relies on.
        isDefined.Should().BeFalse();
    }

    [Fact]
    public void IsDefined_ForNegativeValue_ReturnsFalse()
    {
        // Arrange — negative values are not declared
        const LoginLogOutcome negative = (LoginLogOutcome)(-1);

        // Act
        var isDefined = Enum.IsDefined(negative);

        // Assert
        isDefined.Should().BeFalse();
    }

    // ───────────────────────────────────────────────────────────────────────
    // Underlying type and base type
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Enum_UnderlyingTypeIsInt32()
    {
        // Arrange / Act
        var underlying = Enum.GetUnderlyingType(typeof(LoginLogOutcome));

        // Assert — default enum underlying type is int (System.Int32)
        underlying.Should().Be(typeof(int));
    }

    [Fact]
    public void Enum_DerivesFromSystemEnum()
    {
        // Arrange / Act
        var baseType = typeof(LoginLogOutcome).BaseType;

        // Assert — all C# enums derive from System.Enum
        baseType.Should().Be(typeof(Enum));
    }

    // ───────────────────────────────────────────────────────────────────────
    // String ↔ value conversion (round-trip)
    // ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("InvalidCredentials", LoginLogOutcome.InvalidCredentials)]
    [InlineData("LockedOut", LoginLogOutcome.LockedOut)]
    [InlineData("Success", LoginLogOutcome.Success)]
    [InlineData("Exception", LoginLogOutcome.Exception)]
    [InlineData("NoHttpContext", LoginLogOutcome.NoHttpContext)]
    public void Parse_NameValue_ProducesCorrespondingEnumValue(
        string name, LoginLogOutcome expected)
    {
        // Arrange / Act
        var parsed = Enum.Parse<LoginLogOutcome>(name, ignoreCase: false);

        // Assert — round-trip: name → value
        parsed.Should().Be(expected);
    }

    [Theory]
    [InlineData(LoginLogOutcome.InvalidCredentials, "InvalidCredentials")]
    [InlineData(LoginLogOutcome.LockedOut, "LockedOut")]
    [InlineData(LoginLogOutcome.Success, "Success")]
    [InlineData(LoginLogOutcome.Exception, "Exception")]
    [InlineData(LoginLogOutcome.NoHttpContext, "NoHttpContext")]
    public void ToString_DeclaredValue_ProducesItsName(
        LoginLogOutcome value, string expectedName)
    {
        // Arrange / Act
        var name = value.ToString();

        // Assert — round-trip: value → name (the default Enum.ToString
        // behavior for declared values is the name, not the integer)
        name.Should().Be(expectedName);
    }

    [Fact]
    public void ToString_UndeclaredValue_ProducesIntegerString()
    {
        // Arrange — 999 is undeclared
        const LoginLogOutcome unknown = (LoginLogOutcome)999;

        // Act
        var str = unknown.ToString();

        // Assert — default Enum.ToString behavior for undeclared values is
        // the integer literal as a string (NOT "unknown" — that would be
        // confusing; "999" makes it obvious the value is out-of-range).
        str.Should().Be("999");
    }

    // ───────────────────────────────────────────────────────────────────────
    // Disjointness — no two members share a value (no [Flags] aliases)
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void AllDeclaredValues_HaveDistinctNumericValues()
    {
        // Arrange — security invariant: every declared value is distinct.
        // A future addition using [Flags] would break this and reduce the
        // audit log's precision — that's an SUT design smell.
        var values = Enum.GetValues<LoginLogOutcome>();

        // Act
        var distinctInts = values.Select(v => (int)v).Distinct().Count();

        // Assert
        distinctInts.Should().Be(values.Length);
    }
}
