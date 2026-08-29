using System;
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
/// Money is mapped as a <c>ComplexProperty</c> on FOUR entities in this
/// codebase (NOT <c>OwnsOne</c>): <c>Product.Price</c>,
/// <c>SaleLineItem.UnitPrice</c>, <c>Sale.Total</c>, and
/// <c>CustomerGroup.Salary</c>. <c>ComplexProperty</c> (EF Core 9+) has
/// VALUE SEMANTICS: EF Core compares complex type instances BY VALUE
/// (using <see cref="BaseValueObject"/>'s <c>GetEqualityComponents</c>
/// override), so reference replacement (e.g. <c>Total = sum + line.GrossTotal</c>)
/// works correctly — EF detects the value change and generates a clean
/// UPDATE. <c>OwnsOne</c> tracked by reference identity, which broke
/// this pattern with <c>DbUpdateConcurrencyException</c>.
/// </para>
/// <para>
/// <b>WHY <c>private set</c> ON THE PROPERTIES:</b> EF Core's
/// <c>ComplexProperty</c> materialization needs to set the properties
/// when reconstructing from the database. The <c>private set</c> lets
/// EF populate via reflection while keeping Money immutable to all
/// application and domain code (the class is sealed; no Money method
/// calls the setters after construction).
/// </para>
/// <para>
/// <b>EXCEPTION POLICY:</b> The constructor throws
/// <see cref="DomainException"/> for invalid currency arguments
/// (null/empty currency, wrong-length currency code). These surface to
/// callers as business-rule violations that handlers translate into
/// <c>Result.Failure</c> — keeping the user-facing error path
/// consistent and localizable. <see cref="EnsureSameCurrency"/> also
/// throws <see cref="DomainException"/> for the same reason: mixing
/// currencies in arithmetic is a business-rule violation.
/// </para>
/// <para>
/// <b>NEGATIVE AMOUNTS ARE ALLOWED (deliberate):</b> Money is a general
/// value object. Non-negativity is an invariant of the AGGREGATES that
/// own the money (Product.Price, CustomerGroup.Salary, SaleLineItem.
/// UnitPrice each enforce their own non-negative guards such as
/// <c>EnsurePriceValid</c>), NOT of Money itself. This lets arithmetic
/// represent intermediate negatives (deficits, deltas, budget
/// remaining) without throwing, while the aggregate factories and
/// mutation methods remain the single source of truth for what values
/// are legal at rest.
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
        // Currency guards — DomainException (not ArgumentException) so that
        // a bad currency from any code path (validator bypass, legacy data,
        // integration feed) surfaces as a translatable business-rule failure
        // that handlers can map to Result.Failure, rather than an opaque
        // programmer error. This matches the contract encoded by the test
        // suite (MoneyTests) and keeps a single user-facing error channel.
        if (string.IsNullOrWhiteSpace(currency))
            throw new DomainException("Currency cannot be empty.");
        if (currency.Length != 3)
            throw new DomainException("Currency must be a 3-letter ISO code.");

        // NOTE: negative amounts are intentionally allowed here — see the
        // class-level remarks. Aggregates own their non-negativity guards.
        Amount = amount;
        Currency = currency.ToUpperInvariant();
    }



    // ==================================================================================================================================
    //                                                          OPERATORS
    // ==================================================================================================================================



    public static Money operator +(Money left, Money right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        EnsureSameCurrency(left, right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    public static Money operator *(Money unitPrice, int quantity)
    {
        ArgumentNullException.ThrowIfNull(unitPrice);
        return new Money(unitPrice.Amount * quantity, unitPrice.Currency);
    }

    public static Money operator *(int quantity, Money unitPrice)
    {
        ArgumentNullException.ThrowIfNull(unitPrice);
        return new Money(unitPrice.Amount * quantity, unitPrice.Currency);
    }

    public static Money operator *(decimal multiplier, Money unitPrice)
    {
        ArgumentNullException.ThrowIfNull(unitPrice);
        return new Money(unitPrice.Amount * multiplier, unitPrice.Currency);
    }

    public static Money operator *(Money unitPrice, decimal multiplier)
    {
        ArgumentNullException.ThrowIfNull(unitPrice);
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
