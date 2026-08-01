using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.Primitives;

namespace TakOne.SharedKernel.ValueObjects;

public sealed class Money : BaseValueObject
{



    // ==================================================================================================================================
    //                                                          IMMUTABLE PROPERTIES
    // ==================================================================================================================================



    // ------------------------------------------------------------------
    // WHY PRIVATE SETTERS (NOT GET-ONLY):
    //   Money is an immutable VALUE OBJECT — application code never mutates
    //   an existing Money instance; it always creates a new one via the
    //   arithmetic operators (+, -, *) or the constructor. So the
    //   `private set` here is NOT callable from any external code path or
    //   even from a subclass (the class is sealed).
    //
    //   The reason we use `private set` instead of get-only auto-properties
    //   is EF Core's change tracker. Money is mapped as an OwnsOne on THREE
    //   entities in this codebase:
    //     Product.Price#Money
    //     SaleLineItem.UnitPrice#Money
    //     Sale.Total#Money
    //
    //   When the Sale aggregate recalculates its Total (Sale.RecalculateTotal),
    //   it REPLACES the Total reference:
    //
    //       Total = _lineItems.Aggregate(Money.Zero(currency), (s, i) => s + i.GrossTotal);
    //
    //   The `+` operator returns a BRAND NEW Money instance. So `Sale.Total`
    //   now points to a different object than the one EF Core has tracked.
    //
    //   With GET-ONLY properties, EF Core CANNOT update the OLD tracked
    //   Money instance's Amount/Currency to match the new reference — there
    //   are no setters, not even private ones. The change tracker then gets
    //   confused between the OLD tracked instance (state=Unchanged) and the
    //   NEW reference assigned to Sale.Total, and at SaveChanges it generates
    //   an UPDATE that affects 0 rows:
    //
    //       DbUpdateConcurrencyException:
    //       "The database operation was expected to affect 1 row(s),
    //        but actually affected 0 row(s)"
    //
    //   With PRIVATE SETTERS, EF Core's change tracker can (via reflection)
    //   update the OLD tracked Money instance's Amount and Currency to match
    //   the NEW reference's values. The tracked instance becomes Modified,
    //   and SaveChanges generates a correct UPDATE that affects 1 row.
    //
    //   This preserves Money's immutability invariant for ALL application
    //   and domain code (the setters are private — only Money itself can
    //   call them, and it never does after construction). EF Core is the
    //   sole exception, using reflection to mutate the tracked instance.
    //
    //   The parameterless constructor below is for EF Core's fallback
    //   materialization path (when it can't match the parameterful
    //   constructor). It's private so external code can't create an invalid
    //   Money (one that bypasses the currency validation in the public
    //   constructor).
    // ------------------------------------------------------------------
    public decimal Amount { get; private set; }
    public string Currency { get; private set; }



    // ==================================================================================================================================
    //                                                          CONSTRUCTORS
    // ==================================================================================================================================



    // Parameterless constructor for EF Core materialization fallback.
    // PRIVATE so external code can't bypass the validation in the public
    // constructor. EF Core accesses it via reflection.
#pragma warning disable CS8618
    private Money() { }
#pragma warning restore CS8618

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