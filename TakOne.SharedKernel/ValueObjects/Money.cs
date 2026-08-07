using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.Primitives;

namespace TakOne.SharedKernel.ValueObjects;

/// <summary>
/// An immutable monetary value object representing an amount in a
/// specific currency. Money is a VALUE OBJECT in the DDD sense: two
/// Money instances with the same Amount and Currency are interchangeable,
/// and an existing Money instance is NEVER mutated after construction —
/// "changing" a Money value always produces a brand-new instance via the
/// arithmetic operators (+, -, *) or the constructor.
/// </summary>
/// <remarks>
/// <para>
/// <b>EF CORE MAPPING — <c>ComplexProperty</c> (value semantics):</b>
/// </para>
/// <para>
/// Money is mapped as a <c>ComplexProperty</c> on three entities in this
/// codebase (NOT <c>OwnsOne</c>):
/// <list type="bullet">
///   <item><c>Product.Price</c></item>
///   <item><c>SaleLineItem.UnitPrice</c></item>
///   <item><c>Sale.Total</c></item>
/// </list>
/// </para>
/// <para>
/// <c>ComplexProperty</c> was introduced in EF Core 9 specifically for
/// value objects. Unlike <c>OwnsOne</c>, it has VALUE SEMANTICS: EF Core
/// compares complex type instances BY VALUE (using
/// <see cref="BaseValueObject"/>'s <c>GetEqualityComponents</c> override)
/// instead of by reference identity. Replacing the reference — e.g.
/// <c>Sale.Total = _lineItems.Aggregate(Money.Zero(currency), ...)</c>
/// — works correctly: EF detects that the new instance's value differs
/// from the original snapshot's value and generates a clean UPDATE
/// against the parent row's columns.
/// </para>
/// <para>
/// <b>WHY NOT <c>OwnsOne</c>?</b> <c>OwnsOne</c> tracks owned types by
/// REFERENCE IDENTITY. When domain code replaces the reference, the
/// change tracker has TWO instances for the same navigation (the old
/// tracked one and the new one), and at <c>SaveChanges</c> it generates
/// an UPDATE whose WHERE clause matches 0 rows, producing:
/// <c>DbUpdateConcurrencyException: expected to affect 1 row(s), but
/// actually affected 0 row(s)</c>. <c>ComplexProperty</c> fixes this
/// cleanly — value semantics mean reference replacement is the correct
/// and idiomatic mutation pattern.
/// </para>
/// <para>
/// <b>WHY <c>private set</c> ON THE PROPERTIES (not get-only):</b>
/// EF Core's <c>ComplexProperty</c> materialization path needs to set
/// the properties when reconstructing the value from the database. With
/// get-only auto-properties, EF would have no way to populate them (it
/// would have to use the parameterful constructor, which performs
/// validation that the DB snapshot may not satisfy — e.g. legacy data
/// with a non-uppercase currency code). The <c>private set</c> lets EF
/// populate via reflection while keeping Money immutable to all
/// application and domain code (the class is sealed; the setters are
/// only accessible from within Money itself, and no Money method calls
/// them after construction).
/// </para>
/// <para>
/// <b>NO <c>Update()</c> METHOD:</b> A previous (rejected) fix added
/// an <c>internal void Update(decimal, string)</c> method that mutated
/// the instance in place. That violated value object immutability and
/// was reverted in favor of the <c>ComplexProperty</c> migration. The
/// correct way to "change" a Money value is to construct a new instance
/// and assign it: <c>Total = new Total + line.GrossTotal</c>.
/// </para>
/// </remarks>
public sealed class Money : BaseValueObject
{



    // ==================================================================================================================================
    //                                                          IMMUTABLE PROPERTIES
    // ==================================================================================================================================



    // ------------------------------------------------------------------
    // `private set` is for EF Core's ComplexProperty materialization path
    // — see the class-level XML doc for the full rationale. The class is
    // sealed and the setters are only accessible from within Money itself;
    // no Money method calls them after construction. Money is therefore
    // immutable to all application and domain code.
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