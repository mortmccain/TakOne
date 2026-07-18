using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.Primitives;

namespace TakOne.SharedKernel.ValueObjects;

public sealed class Money : BaseValueObject
{



    // ==================================================================================================================================
    //                                                          IMMUTABLE PROPERTIES
    // ==================================================================================================================================



    //the whole null issue. rethink this later
    public decimal Amount { get; }
    public string Currency { get; }



    // ==================================================================================================================================
    //                                                          CONSTRUCTORS
    // ==================================================================================================================================



    public Money(decimal amount, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency)) throw new DomainException("Currency cannot be empty.");

        if (currency.Length != 3) throw new DomainException("Currency must be a 3-letter ISO code.");

        Amount = amount;
        Currency = currency.ToUpperInvariant();
    }



    // ==================================================================================================================================
    //                                                          OPERATORS
    // ==================================================================================================================================



    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    //validations and checks need to be reviewed
    public static Money operator *(Money unitPrice, int quantity)
    {
        return new Money(unitPrice.Amount * quantity, unitPrice.Currency);
    }

    public static Money operator *(int quantity, Money unitPrice)
    {
        return new Money(unitPrice.Amount * quantity, unitPrice.Currency);
    }

    public static Money operator *(decimal multiplier, Money unitPrice)
    {
        return new Money(unitPrice.Amount * multiplier, unitPrice.Currency);
    }

    public static Money operator *(Money unitPrice, decimal multiplier)
    {
        return new Money(unitPrice.Amount * multiplier, unitPrice.Currency);
    }



    // ==================================================================================================================================
    //                                                          CENTRALIZED GUARD METHODS
    // ==================================================================================================================================



    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
            throw new DomainException(
                $"Cannot operate on different currencies: {left.Currency} and {right.Currency}.");
    }



    // ==================================================================================================================================
    //                                                          Value Object Infrastructure
    // ==================================================================================================================================



    public static Money Zero(string currency) => new(0m, currency);

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Currency;
        yield return Amount;
    }



    // ==================================================================================================================================
    //                                                          ANYTHING ELSE   
    // ================================================================================================================================



    public override string ToString() => $"{Amount:F2} {Currency}";

}