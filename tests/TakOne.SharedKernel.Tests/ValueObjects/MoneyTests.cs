using FluentAssertions;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;

namespace TakOne.SharedKernel.Tests.ValueObjects;

/// <summary>
/// Unit tests for <see cref="Money"/> — the immutable monetary value object.
/// Verifies the constructor validation guards, currency upper-casing, the
/// Zero factory, all arithmetic operators (+, -, *, symmetric *), ToString
/// format, value-object equality, and immutability semantics.
/// </summary>
public class MoneyTests
{
    [Fact]
    public void Constructor_WhenGivenAmountAndCurrency_SetsBothProperties()
    {
        // Arrange
        const decimal amount = 12.5m;

        // Act
        var money = new Money(amount, TestValues.USD);

        // Assert
        money.Amount.Should().Be(amount);
        money.Currency.Should().Be(TestValues.USD);
    }

    [Fact]
    public void Constructor_WhenGivenLowercaseCurrency_UpperCasesCurrency()
    {
        // Arrange
        const string lowercase = "usd";

        // Act
        var money = new Money(10m, lowercase);

        // Assert
        // ToUpperInvariant() normalizes the currency code to ISO 4217 form.
        money.Currency.Should().Be("USD");
    }

    [Fact]
    public void Constructor_WhenGivenMixedCaseCurrency_UpperCasesCurrency()
    {
        // Arrange
        const string mixed = "uSd";

        // Act
        var money = new Money(1m, mixed);

        // Assert
        money.Currency.Should().Be("USD");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    public void Constructor_WhenCurrencyIsEmptyOrWhitespace_ThrowsDomainException(string currency)
    {
        // Arrange + Act
        var act = () => new Money(1m, currency);

        // Assert
        // string.IsNullOrWhiteSpace is the guard — empty, space, and tab
        // all trigger the same DomainException.
        act.Should().Throw<DomainException>()
            .WithMessage("Currency cannot be empty.");
    }

    [Theory]
    [InlineData("AB")]    // too short
    [InlineData("ABCD")]  // too long
    [InlineData("A")]     // way too short
    [InlineData("ABCDE")] // way too long
    public void Constructor_WhenCurrencyLengthIsNot3_ThrowsDomainException(string currency)
    {
        // Arrange + Act
        var act = () => new Money(1m, currency);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Currency must be a 3-letter ISO code.");
    }

    [Fact]
    public void Constructor_WhenCurrencyIsNull_ThrowsDomainException()
    {
        // Arrange + Act
        // string.IsNullOrWhiteSpace(null) returns true → same guard path
        // as the empty-currency case.
        var act = () => new Money(1m, null!);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Currency cannot be empty.");
    }

    [Fact]
    public void Zero_WhenGivenValidCurrency_ReturnsMoneyWithZeroAmount()
    {
        // Arrange + Act
        var zero = Money.Zero(TestValues.IRR);

        // Assert
        zero.Amount.Should().Be(0m);
        zero.Currency.Should().Be(TestValues.IRR);
    }

    [Fact]
    public void Zero_WhenGivenLowercaseCurrency_UpperCasesCurrency()
    {
        // Arrange + Act
        var zero = Money.Zero("irr");

        // Assert
        zero.Currency.Should().Be("IRR");
    }

    [Fact]
    public void Zero_WhenGivenEmptyCurrency_ThrowsDomainException()
    {
        // Arrange + Act
        // Zero(currency) is a thin wrapper around the ctor — guards apply.
        var act = () => Money.Zero(string.Empty);

        // Assert
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void OperatorPlus_WhenSameCurrency_ReturnsSummedAmount()
    {
        // Arrange
        var a = new Money(10m, TestValues.USD);
        var b = new Money(5.5m, TestValues.USD);

        // Act
        var result = a + b;

        // Assert
        result.Amount.Should().Be(15.5m);
        result.Currency.Should().Be(TestValues.USD);
    }

    [Fact]
    public void OperatorPlus_WhenDifferentCurrencies_ThrowsDomainException()
    {
        // Arrange
        var a = new Money(1m, TestValues.USD);
        var b = new Money(2m, TestValues.EUR);

        // Act
        var act = () => (a + b);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Cannot operate on different currencies: USD and EUR.");
    }

    [Fact]
    public void OperatorMinus_WhenSameCurrency_ReturnsSubtractedAmount()
    {
        // Arrange
        var a = new Money(10m, TestValues.USD);
        var b = new Money(3m, TestValues.USD);

        // Act
        var result = a - b;

        // Assert
        result.Amount.Should().Be(7m);
        result.Currency.Should().Be(TestValues.USD);
    }

    [Fact]
    public void OperatorMinus_WhenDifferentCurrencies_ThrowsDomainException()
    {
        // Arrange
        var a = new Money(10m, TestValues.USD);
        var b = new Money(3m, TestValues.IRR);

        // Act
        var act = () => (a - b);

        // Assert
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void OperatorMultiply_WhenMoneyTimesInt_ReturnsMoneyWithMultipliedAmount()
    {
        // Arrange
        var unit = new Money(2.5m, TestValues.USD);
        const int quantity = 4;

        // Act
        var result = unit * quantity;

        // Assert
        result.Amount.Should().Be(10m);
        result.Currency.Should().Be(TestValues.USD);
    }

    [Fact]
    public void OperatorMultiply_WhenIntTimesMoney_ReturnsSameAsMoneyTimesInt()
    {
        // Arrange
        var unit = new Money(2.5m, TestValues.USD);
        const int quantity = 4;

        // Act
        var leftToRight = quantity * unit;

        // Assert
        // The symmetric operator returns a Money with the same Amount and
        // Currency as the canonical Money-first ordering.
        leftToRight.Amount.Should().Be(10m);
        leftToRight.Currency.Should().Be(TestValues.USD);
    }

    [Fact]
    public void OperatorMultiply_WhenMoneyTimesDecimal_ReturnsMoneyWithMultipliedAmount()
    {
        // Arrange
        var unit = new Money(2m, TestValues.IRR);
        const decimal multiplier = 1.5m;

        // Act
        var result = unit * multiplier;

        // Assert
        result.Amount.Should().Be(3m);
        result.Currency.Should().Be(TestValues.IRR);
    }

    [Fact]
    public void OperatorMultiply_WhenDecimalTimesMoney_ReturnsSameAsMoneyTimesDecimal()
    {
        // Arrange
        var unit = new Money(2m, TestValues.IRR);
        const decimal multiplier = 1.5m;

        // Act
        var result = multiplier * unit;

        // Assert
        result.Amount.Should().Be(3m);
        result.Currency.Should().Be(TestValues.IRR);
    }

    [Fact]
    public void OperatorMultiply_WhenMultipliedByZero_ReturnsZeroAmount()
    {
        // Arrange
        var unit = new Money(100m, TestValues.USD);
        const int quantity = 0;

        // Act
        var result = unit * quantity;

        // Assert
        result.Amount.Should().Be(0m);
        result.Currency.Should().Be(TestValues.USD);
    }

    [Fact]
    public void OperatorMultiply_WhenMultipliedByNegative_ReturnsNegativeAmount()
    {
        // Arrange
        var unit = new Money(10m, TestValues.USD);
        const int quantity = -2;

        // Act
        var result = unit * quantity;

        // Assert
        result.Amount.Should().Be(-20m);
        result.Currency.Should().Be(TestValues.USD);
    }

    [Fact]
    public void ToString_WhenAmountHasFractionalDigits_FormatsWithTwoDecimalsAndCurrency()
    {
        // Arrange
        var money = new Money(12.5m, TestValues.USD);

        // Act
        var s = money.ToString();

        // Assert
        // ToString returns $"{Amount:F2} {Currency}" — the F2 format uses the
        // current thread culture; expected is computed with the same format
        // to avoid culture-flakiness.
        s.Should().Be($"{12.5m:F2} USD");
    }

    [Fact]
    public void ToString_WhenAmountIsWholeNumber_StillFormatsWithTwoDecimals()
    {
        // Arrange
        var money = new Money(10m, TestValues.IRR);

        // Act
        var s = money.ToString();

        // Assert
        s.Should().Be($"{10m:F2} IRR");
    }

    [Fact]
    public void ToString_WhenAmountIsZero_FormatsAs0_00AndCurrency()
    {
        // Arrange
        var zero = Money.Zero(TestValues.USD);

        // Act
        var s = zero.ToString();

        // Assert
        s.Should().Be($"{0m:F2} USD");
    }

    [Fact]
    public void Equality_WhenAmountAndCurrencyAreSame_ReturnsTrue()
    {
        // Arrange
        var a = new Money(10m, TestValues.USD);
        var b = new Money(10m, TestValues.USD);

        // Act + Assert
        a.Equals(b).Should().BeTrue();
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void Inequality_WhenAmountSameButCurrencyDifferent_ReturnsNotEqual()
    {
        // Arrange
        var a = new Money(10m, TestValues.USD);
        var b = new Money(10m, TestValues.EUR);

        // Act + Assert
        a.Equals(b).Should().BeFalse();
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void Inequality_WhenCurrencySameButAmountDifferent_ReturnsNotEqual()
    {
        // Arrange
        var a = new Money(10m, TestValues.USD);
        var b = new Money(20m, TestValues.USD);

        // Act + Assert
        a.Equals(b).Should().BeFalse();
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void GetHashCode_WhenCalledTwiceOnSameInstance_ReturnsSameValue()
    {
        // Arrange
        var money = new Money(10m, TestValues.USD);

        // Act
        var h1 = money.GetHashCode();
        var h2 = money.GetHashCode();

        // Assert
        h1.Should().Be(h2);
    }

    [Fact]
    public void GetHashCode_WhenTwoEqualMoneys_ReturnsSameHash()
    {
        // Arrange
        var a = new Money(10m, TestValues.USD);
        var b = new Money(10m, TestValues.USD);

        // Act + Assert
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void EqualsOperator_WhenBothOperandsAreNull_ReturnsTrue()
    {
        // Arrange
        Money? a = null;
        Money? b = null;

        // Act + Assert
#pragma warning disable CS8604 // Operator signature is non-nullable but implementation handles null.
        (a == b).Should().BeTrue();
#pragma warning restore CS8604
    }

    [Fact]
    public void EqualsOperator_WhenOneOperandIsNull_ReturnsFalse()
    {
        // Arrange
        Money? a = null;
        var b = new Money(1m, TestValues.USD);

        // Act + Assert
#pragma warning disable CS8604 // Operator signature is non-nullable but implementation handles null.
        (a == b).Should().BeFalse();
        (b == a).Should().BeFalse();
#pragma warning restore CS8604
    }

    [Fact]
    public void OperatorPlus_ReturnsNewInstance_DoesNotMutateOperands()
    {
        // Arrange
        // Money is immutable: arithmetic operators return a brand-new Money.
        var a = new Money(10m, TestValues.USD);
        var b = new Money(5m, TestValues.USD);

        // Act
        var result = a + b;

        // Assert
        result.Should().NotBeSameAs(a);
        result.Should().NotBeSameAs(b);
        a.Amount.Should().Be(10m);
        b.Amount.Should().Be(5m);
    }

    [Fact]
    public void Zero_WithSameCurrency_AsActualMoney_IsNotEqualWhenAmountNonZero()
    {
        // Arrange
        var zero = Money.Zero(TestValues.USD);
        var nonZero = new Money(0.01m, TestValues.USD);

        // Act + Assert
        // Zero returns Money with Amount=0, but Equals still works on
        // components — any non-zero amount differs.
        zero.Equals(nonZero).Should().BeFalse();
    }

    [Fact]
    public void Zero_WithCurrencyMatchingAnotherZero_AreEqual()
    {
        // Arrange
        var z1 = Money.Zero(TestValues.USD);
        var z2 = Money.Zero(TestValues.USD);

        // Act + Assert
        z1.Equals(z2).Should().BeTrue();
        (z1 == z2).Should().BeTrue();
    }

    [Fact]
    public void Constructor_WhenGivenNegativeAmount_AllowsIt()
    {
        // Arrange
        // Money ctor does NOT guard against negative amounts — credits and
        // debits can legitimately be negative.
        const decimal amount = -100m;

        // Act
        var money = new Money(amount, TestValues.USD);

        // Assert
        money.Amount.Should().Be(amount);
        money.Currency.Should().Be(TestValues.USD);
    }

    [Fact]
    public void Constructor_WhenGivenZeroAmount_AllowsIt()
    {
        // Arrange
        const decimal amount = 0m;

        // Act
        var money = new Money(amount, TestValues.USD);

        // Assert
        money.Amount.Should().Be(0m);
        money.Currency.Should().Be(TestValues.USD);
    }
}
