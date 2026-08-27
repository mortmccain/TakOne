using System.Reflection;
using FluentAssertions;
using TakOne.Testing;
using TakOne.WebUI.Services.Logging;
using Xunit;

namespace TakOne.WebUI.Tests.Logging;

/// <summary>
/// Unit tests for <see cref="LoginLogContext"/> — the sealed record that
/// defines the allow-list of fields <see cref="LoginAuditLogger"/> may
/// emit to the audit log.
/// </summary>
/// <remarks>
/// <para>
/// <b>SUT scope.</b> A sealed record with two REQUIRED init-only
/// properties (<see cref="LoginLogContext.WorkerId"/>,
/// <see cref="LoginLogContext.Outcome"/>) and three optional init-only
/// properties (<see cref="LoginLogContext.UserId"/> of type
/// <see cref="Guid"/>, <see cref="LoginLogContext.ExceptionTypeName"/>
/// of type <c>string?</c>, <see cref="LoginLogContext.MustChangePassword"/>
/// of type <see cref="bool"/> with default false).
/// </para>
/// <para>
/// <b>Defense-by-construction.</b> The "required" C# 11 keyword enforces
/// that callers MUST set WorkerId and Outcome at construction time — the
/// compiler rejects any construction that omits them. There's no runtime
/// null-guard in the constructor; the keyword is the contract. We verify
/// the keyword is in place via reflection (the
/// <see cref="RequiredMemberAttribute"/> attribute is applied).
/// </para>
/// </remarks>
public class LoginLogContextTests
{
    // ───────────────────────────────────────────────────────────────────────
    // Construction — required fields
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_WithWorkerIdAndOutcomeOnly_LeavesOptionalFieldsAtDefaults()
    {
        // Arrange — minimum-viable construction (the two required fields only)
        // Act
        var ctx = new LoginLogContext
        {
            WorkerId = "user123",
            Outcome = LoginLogOutcome.Success
        };

        // Assert — optional fields default to null/false
        ctx.WorkerId.Should().Be("user123");
        ctx.Outcome.Should().Be(LoginLogOutcome.Success);
        ctx.UserId.Should().BeNull();
        ctx.ExceptionTypeName.Should().BeNull();
        ctx.MustChangePassword.Should().BeFalse();
    }

    [Fact]
    public void Constructor_WithAllFields_SetsAllProperties()
    {
        // Arrange / Act — exercise every property in the record
        var ctx = new LoginLogContext
        {
            WorkerId = "user123",
            Outcome = LoginLogOutcome.Exception,
            UserId = TestValues.UserId,
            ExceptionTypeName = "SqlException",
            MustChangePassword = true
        };

        // Assert
        ctx.WorkerId.Should().Be("user123");
        ctx.Outcome.Should().Be(LoginLogOutcome.Exception);
        ctx.UserId.Should().Be(TestValues.UserId);
        ctx.ExceptionTypeName.Should().Be("SqlException");
        ctx.MustChangePassword.Should().BeTrue();
    }

    // ───────────────────────────────────────────────────────────────────────
    // Required-keyword verification (compile-time contract enforced via the
    // RequiredMemberAttribute presence on the property setters)
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void WorkerId_IsMarkedRequired_ViaCSharp11RequiredKeyword()
    {
        // Arrange — verify the `required` keyword is on WorkerId by
        // inspecting the RequiredMemberAttribute on the property. This is
        // a compile-time contract, but the attribute IS preserved on the
        // property setter post-compilation, so we can assert it via
        // reflection. (The C# 11 `required` keyword lowers to the
        // RequiredMemberAttribute + SetsRequiredMembersAttribute on the
        // property accessor.)
        var prop = typeof(LoginLogContext).GetProperty(nameof(LoginLogContext.WorkerId));

        // Act
        var isRequired = prop?.GetSetMethod()?.IsSpecialName == true
            && prop.GetCustomAttributes(true).Any(a => a.GetType().Name == "RequiredMemberAttribute")
            && Attribute.IsDefined(prop, typeof(System.Runtime.CompilerServices.RequiredMemberAttribute));

        // Assert — WorkerId must be marked with the required attribute
        isRequired.Should().BeTrue();
    }

    [Fact]
    public void Outcome_IsMarkedRequired_ViaCSharp11RequiredKeyword()
    {
        // Arrange
        var prop = typeof(LoginLogContext).GetProperty(nameof(LoginLogContext.Outcome));

        // Act
        var isRequired = Attribute.IsDefined(prop!, typeof(System.Runtime.CompilerServices.RequiredMemberAttribute));

        // Assert — Outcome must be marked with the required attribute
        isRequired.Should().BeTrue();
    }

    [Fact]
    public void UserId_IsNotMarkedRequired()
    {
        // Arrange — UserId is optional (it's only set on Success outcomes,
        // NEVER on InvalidCredentials, per the SUT docstring)
        var prop = typeof(LoginLogContext).GetProperty(nameof(LoginLogContext.UserId));

        // Act
        var isRequired = Attribute.IsDefined(prop!, typeof(System.Runtime.CompilerServices.RequiredMemberAttribute));

        // Assert — optional fields must NOT have the required attribute
        isRequired.Should().BeFalse();
    }

    [Fact]
    public void ExceptionTypeName_IsNotMarkedRequired()
    {
        // Arrange
        var prop = typeof(LoginLogContext).GetProperty(nameof(LoginLogContext.ExceptionTypeName));

        // Act
        var isRequired = Attribute.IsDefined(prop!, typeof(System.Runtime.CompilerServices.RequiredMemberAttribute));

        // Assert
        isRequired.Should().BeFalse();
    }

    [Fact]
    public void MustChangePassword_IsNotMarkedRequired()
    {
        // Arrange
        var prop = typeof(LoginLogContext).GetProperty(nameof(LoginLogContext.MustChangePassword));

        // Act
        var isRequired = Attribute.IsDefined(prop!, typeof(System.Runtime.CompilerServices.RequiredMemberAttribute));

        // Assert
        isRequired.Should().BeFalse();
    }

    // ───────────────────────────────────────────────────────────────────────
    // Defaults — non-required fields
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void UserId_DefaultIsNull()
    {
        // Arrange / Act
        var ctx = new LoginLogContext { WorkerId = "w", Outcome = LoginLogOutcome.Success };

        // Assert
        ctx.UserId.Should().BeNull();
    }

    [Fact]
    public void ExceptionTypeName_DefaultIsNull()
    {
        // Arrange / Act
        var ctx = new LoginLogContext { WorkerId = "w", Outcome = LoginLogOutcome.Success };

        // Assert
        ctx.ExceptionTypeName.Should().BeNull();
    }

    [Fact]
    public void MustChangePassword_DefaultIsFalse()
    {
        // Arrange / Act
        var ctx = new LoginLogContext { WorkerId = "w", Outcome = LoginLogOutcome.Success };

        // Assert
        ctx.MustChangePassword.Should().BeFalse();
    }

    // ───────────────────────────────────────────────────────────────────────
    // Record equality and `with` semantics
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Equality_TwoInstancesWithSameValues_AreEqualViaEqualsOperator()
    {
        // Arrange
        var a = new LoginLogContext
        {
            WorkerId = "w",
            Outcome = LoginLogOutcome.Success,
            UserId = TestValues.UserId,
            ExceptionTypeName = null,
            MustChangePassword = false
        };
        var b = new LoginLogContext
        {
            WorkerId = "w",
            Outcome = LoginLogOutcome.Success,
            UserId = TestValues.UserId,
            ExceptionTypeName = null,
            MustChangePassword = false
        };

        // Act / Assert — records have value-based equality via ==
        (a == b).Should().BeTrue();
        a.Equals(b).Should().BeTrue();
    }

    [Fact]
    public void Inequality_OneFieldDifferent_NotEqualViaEqualsOperator()
    {
        // Arrange
        var a = new LoginLogContext { WorkerId = "w", Outcome = LoginLogOutcome.Success };
        var b = new LoginLogContext { WorkerId = "w", Outcome = LoginLogOutcome.InvalidCredentials };

        // Act / Assert — different Outcome → not equal
        (a != b).Should().BeTrue();
        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void Inequality_DifferentWorkerId_NotEqual()
    {
        // Arrange
        var a = new LoginLogContext { WorkerId = "w1", Outcome = LoginLogOutcome.Success };
        var b = new LoginLogContext { WorkerId = "w2", Outcome = LoginLogOutcome.Success };

        // Act / Assert
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void Inequality_DifferentOptionalField_NotEqual()
    {
        // Arrange — required fields identical; only the optional UserId differs
        var a = new LoginLogContext { WorkerId = "w", Outcome = LoginLogOutcome.Success, UserId = TestValues.UserId };
        var b = new LoginLogContext { WorkerId = "w", Outcome = LoginLogOutcome.Success, UserId = null };

        // Act / Assert
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void WithExpression_CreatesCopyWithOneFieldChanged()
    {
        // Arrange
        var original = new LoginLogContext
        {
            WorkerId = "w",
            Outcome = LoginLogOutcome.Success,
            UserId = TestValues.UserId,
            ExceptionTypeName = null,
            MustChangePassword = false
        };

        // Act — the `with` syntax produces a new record with one property
        // overridden; everything else is propagated from `original`.
        var modified = original with { MustChangePassword = true };

        // Assert — only MustChangePassword is changed
        modified.WorkerId.Should().Be(original.WorkerId);
        modified.Outcome.Should().Be(original.Outcome);
        modified.UserId.Should().Be(original.UserId);
        modified.ExceptionTypeName.Should().Be(original.ExceptionTypeName);
        modified.MustChangePassword.Should().BeTrue();
        original.MustChangePassword.Should().BeFalse("the original must be untouched");
    }

    [Fact]
    public void WithExpression_CreatesCopyWithUserIdCleared()
    {
        // Arrange — a Success context where UserId was set; simulate the
        // login flow's InvalidCredentials path where UserId must be NULL.
        var success = new LoginLogContext
        {
            WorkerId = "w",
            Outcome = LoginLogOutcome.Success,
            UserId = TestValues.UserId,
            MustChangePassword = false
        };

        // Act — clear UserId and change Outcome via `with`
        var failure = success with { Outcome = LoginLogOutcome.InvalidCredentials, UserId = null };

        // Assert
        failure.UserId.Should().BeNull();
        failure.Outcome.Should().Be(LoginLogOutcome.InvalidCredentials);
        failure.WorkerId.Should().Be(success.WorkerId);
    }

    // ───────────────────────────────────────────────────────────────────────
    // GetHashCode — record default behavior
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void GetHashCode_EqualRecords_HaveSameHashCode()
    {
        // Arrange
        var a = new LoginLogContext { WorkerId = "w", Outcome = LoginLogOutcome.Success, UserId = TestValues.UserId };
        var b = new LoginLogContext { WorkerId = "w", Outcome = LoginLogOutcome.Success, UserId = TestValues.UserId };

        // Act / Assert
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void GetHashCode_DifferentRecords_HaveDifferentHashCodes()
    {
        // Arrange
        var a = new LoginLogContext { WorkerId = "w1", Outcome = LoginLogOutcome.Success };
        var b = new LoginLogContext { WorkerId = "w2", Outcome = LoginLogOutcome.Success };

        // Act / Assert — different WorkerId values produce different hashes
        // (records include all fields in the hash; same hash collision is
        // astronomically unlikely with a 2-char WorkerId difference)
        a.GetHashCode().Should().NotBe(b.GetHashCode());
    }

    // ───────────────────────────────────────────────────────────────────────
    // ToString — record default behavior
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToString_ContainsAllFieldNames()
    {
        // Arrange
        var ctx = new LoginLogContext
        {
            WorkerId = "user123",
            Outcome = LoginLogOutcome.Exception,
            UserId = TestValues.UserId,
            ExceptionTypeName = "SqlException",
            MustChangePassword = false
        };

        // Act
        var str = ctx.ToString();

        // Assert — record ToString produces "LoginLogContext { Field = value, ... }"
        // Verify the type name and all field names appear (don't pin the
        // exact format; that would over-couple to the runtime's
        // RecordPrinter implementation).
        str.Should().Contain(nameof(LoginLogContext));
        str.Should().Contain(nameof(LoginLogContext.WorkerId));
        str.Should().Contain(nameof(LoginLogContext.Outcome));
        str.Should().Contain(nameof(LoginLogContext.UserId));
        str.Should().Contain(nameof(LoginLogContext.ExceptionTypeName));
        str.Should().Contain(nameof(LoginLogContext.MustChangePassword));
        str.Should().Contain("user123");
        str.Should().Contain("SqlException");
    }

    [Fact]
    public void ToString_ContainsWorkerIdValue()
    {
        // Arrange
        var ctx = new LoginLogContext { WorkerId = "alice", Outcome = LoginLogOutcome.Success };

        // Act
        var str = ctx.ToString();

        // Assert — record ToString interpolates the value
        str.Should().Contain("alice");
    }

    // ───────────────────────────────────────────────────────────────────────
    // Sealed / record semantics
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void LoginLogContext_IsSealed()
    {
        // Arrange — verify the SUT's `sealed` modifier prevents subclassing
        // (defense-by-construction: a subclass could add forbidden fields).
        // Act
        var isSealed = typeof(LoginLogContext).IsSealed;

        // Assert
        isSealed.Should().BeTrue();
    }

    [Fact]
    public void LoginLogContext_IsARecord()
    {
        // Arrange — verify the SUT is declared as a `record` (so it gets
        // value-based equality, `with` support, and the RecordPrinter
        // ToString implementation).
        var type = typeof(LoginLogContext);

        // Act
        var isRecord = type.GetMethod("op_Equality") is not null
            && type.GetMethod("op_Inequality") is not null
            && type.GetMethod("<Clone>$") is not null;

        // Assert — records emit op_Equality, op_Inequality, and <Clone>$
        isRecord.Should().BeTrue();
    }

    // ───────────────────────────────────────────────────────────────────────
    // Property init-only verification (defense-by-construction)
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void WorkerId_HasInitSetterOnly()
    {
        // Arrange — verify the property is init-only (cannot be reassigned
        // after construction). The `init` keyword lowers to the
        // IsExternalInit modreq on the setter — reflection exposes it as
        // a method with no return value and the modreq.
        var prop = typeof(LoginLogContext).GetProperty(nameof(LoginLogContext.WorkerId));
        var setter = prop?.GetSetMethod();

        // Act
        var isInitOnly = setter is not null
            && setter.ReturnParameter.GetRequiredCustomModifiers()
                .Any(t => t.Name == "IsExternalInit");

        // Assert — WorkerId must be init-only
        isInitOnly.Should().BeTrue();
    }

    [Fact]
    public void Outcome_HasInitSetterOnly()
    {
        // Arrange
        var prop = typeof(LoginLogContext).GetProperty(nameof(LoginLogContext.Outcome));
        var setter = prop?.GetSetMethod();

        // Act
        var isInitOnly = setter is not null
            && setter.ReturnParameter.GetRequiredCustomModifiers()
                .Any(t => t.Name == "IsExternalInit");

        // Assert
        isInitOnly.Should().BeTrue();
    }

    [Fact]
    public void AllProperties_AreInitOnly()
    {
        // Arrange — defense-by-construction: every property in the
        // allow-list must be init-only so that consumers cannot mutate a
        // LoginLogContext after construction (the audit log captures an
        // immutable snapshot at every login-flow exit point).
        var props = new[]
        {
            nameof(LoginLogContext.WorkerId),
            nameof(LoginLogContext.Outcome),
            nameof(LoginLogContext.UserId),
            nameof(LoginLogContext.ExceptionTypeName),
            nameof(LoginLogContext.MustChangePassword)
        };

        // Act / Assert — every property is init-only
        foreach (var name in props)
        {
            var prop = typeof(LoginLogContext).GetProperty(name);
            var setter = prop?.GetSetMethod();
            setter.Should().NotBeNull($"property {name} must have a setter");
            var isInitOnly = setter!.ReturnParameter.GetRequiredCustomModifiers()
                .Any(t => t.Name == "IsExternalInit");
            isInitOnly.Should().BeTrue($"property {name} must be init-only");
        }
    }
}
