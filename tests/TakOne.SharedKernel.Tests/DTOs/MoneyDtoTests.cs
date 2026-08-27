using FluentAssertions;
using TakOne.SharedKernel.DTOs;
using Xunit;

namespace TakOne.SharedKernel.Tests.DTOs;

/// <summary>
/// Unit tests for <see cref="MoneyDto"/> — the DTO twin of the
/// <see cref="TakOne.SharedKernel.ValueObjects.Money"/> value object.
/// MoneyDto is a plain init-only DTO with no equality override; this
/// suite verifies the init setters and the default-initialized state.
/// </summary>
public class MoneyDtoTests
{
    [Fact]
    public void DefaultConstructor_WhenCalled_LeavesCurrencyAsEmptyString()
    {
        // Arrange + Act
        var dto = new MoneyDto();

        // Assert
        // The Currency property has an inline initializer "= string.Empty".
        dto.Currency.Should().BeEmpty();
        dto.Currency.Should().Be(string.Empty);
    }

    [Fact]
    public void DefaultConstructor_WhenCalled_LeavesAmountAtZero()
    {
        // Arrange + Act
        var dto = new MoneyDto();

        // Assert
        // decimal default is 0m.
        dto.Amount.Should().Be(0m);
    }

    [Fact]
    public void InitSetters_WhenSettingAmountAndCurrency_PersistValues()
    {
        // Arrange + Act
        var dto = new MoneyDto { Amount = 12.5m, Currency = "USD" };

        // Assert
        dto.Amount.Should().Be(12.5m);
        dto.Currency.Should().Be("USD");
    }

    [Fact]
    public void InitSetter_WhenSettingOnlyAmount_LeavesCurrencyAsEmpty()
    {
        // Arrange + Act
        var dto = new MoneyDto { Amount = 99m };

        // Assert
        dto.Amount.Should().Be(99m);
        dto.Currency.Should().BeEmpty();
    }

    [Fact]
    public void InitSetter_WhenSettingOnlyCurrency_LeavesAmountAtZero()
    {
        // Arrange + Act
        var dto = new MoneyDto { Currency = "EUR" };

        // Assert
        dto.Amount.Should().Be(0m);
        dto.Currency.Should().Be("EUR");
    }

    [Fact]
    public void InitSetters_AreWriteOnce_CannotBeReassignedAfterConstruction()
    {
        // Arrange
        var dto = new MoneyDto { Amount = 1m, Currency = "USD" };

        // Act + Assert
        // The init-only setters cannot be reassigned post-construction —
        // the assignment expression below is a compile error, so we can't
        // even produce an "act" body. This test exists to document that
        // init-only is in effect by referencing the properties for read.
        dto.Amount.Should().Be(1m);
        dto.Currency.Should().Be("USD");
    }

    [Fact]
    public void Currency_CanBeSetToAnyThreeLetterString_NoValidation()
    {
        // Arrange + Act
        // MoneyDto is a plain DTO — it does NOT replicate the Money value
        // object's validation guards. Any string is accepted.
        var dto = new MoneyDto { Amount = 1m, Currency = "xx" };

        // Assert
        dto.Currency.Should().Be("xx");
    }

    [Fact]
    public void Currency_CanBeSetToEmptyString_AfterExplicitConstruction()
    {
        // Arrange + Act
        var dto = new MoneyDto { Amount = 1m, Currency = string.Empty };

        // Assert
        dto.Currency.Should().BeEmpty();
    }

    [Fact]
    public void Currency_CanBeNullWhenExplicitlyAssigned()
    {
        // Arrange + Act
        // The Currency type is `string` (non-nullable in declaration) but
        // the property type allows null assignment at runtime since string
        // is a reference type. This is technically possible and we
        // document that the DTO doesn't prevent it.
#pragma warning disable CS8625
        var dto = new MoneyDto { Currency = null };
#pragma warning restore CS8625

        // Assert
        dto.Currency.Should().BeNull();
    }

    [Fact]
    public void Equals_WhenCalledOnTwoDistinctInstances_FallsBackToReferenceEquality()
    {
        // Arrange
        // MoneyDto is a sealed class that does NOT override Equals — it
        // falls back to object.Equals (reference equality). Two distinct
        // instances with the same values are NOT equal.
        var a = new MoneyDto { Amount = 1m, Currency = "USD" };
        var b = new MoneyDto { Amount = 1m, Currency = "USD" };

        // Act
        var equal = a.Equals(b);

        // Assert
        equal.Should().BeFalse();
    }

    [Fact]
    public void Equals_WhenCalledOnSameInstance_ReturnsTrue()
    {
        // Arrange
        var a = new MoneyDto { Amount = 1m, Currency = "USD" };

        // Act
        var equal = a.Equals(a);

        // Assert
        // ReferenceEquals shortcut — comparing to self is always true.
        equal.Should().BeTrue();
    }
}
