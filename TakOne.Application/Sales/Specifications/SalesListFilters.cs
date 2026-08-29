using TakOne.Application.Sales.Queries.GetSalesPaginated;

namespace TakOne.Application.Sales.Specifications;

/// <summary>
/// Text-match operators for the sales list's server-side column filters
/// (Round 4). Mirrors the Radzen <c>FilterOperator</c> values the grid's
/// filter row can emit for string columns — the WebUI layer translates
/// Radzen's operator enum to this one; the Application layer stays free
/// of Radzen dependencies.
/// </summary>
public enum SalesTextOperator
{
    /// <summary>Substring match (case-insensitive).</summary>
    Contains = 1,

    /// <summary>Excludes rows where the value contains the term.</summary>
    NotContains = 2,

    /// <summary>Exact match (case-insensitive).</summary>
    Equals = 3,

    /// <summary>Excludes rows with the exact value.</summary>
    NotEquals = 4,

    /// <summary>Prefix match (case-insensitive).</summary>
    StartsWith = 5,

    /// <summary>Suffix match (case-insensitive).</summary>
    EndsWith = 6
}

/// <summary>
/// Numeric-comparison operators for the sales list's server-side Total
/// column filter. Mirrors the Radzen <c>FilterOperator</c> values a
/// numeric column's filter menu can emit.
/// </summary>
public enum SalesAmountOperator
{
    /// <summary>Total amount equals the value.</summary>
    Equals = 1,

    /// <summary>Total amount differs from the value.</summary>
    NotEquals = 2,

    /// <summary>Total amount strictly greater than the value.</summary>
    GreaterThan = 3,

    /// <summary>Total amount greater than or equal to the value.</summary>
    GreaterThanOrEqual = 4,

    /// <summary>Total amount strictly less than the value.</summary>
    LessThan = 5,

    /// <summary>Total amount less than or equal to the value.</summary>
    LessThanOrEqual = 6
}

/// <summary>
/// A single server-side text column filter (term + operator).
/// </summary>
/// <param name="Value">The raw term as typed. Trimmed by the spec
/// helpers; an empty/whitespace term means "no filter" (lenient
/// contract, same as the pre-Round-4 search box).</param>
/// <param name="Operator">How <paramref name="Value"/> is matched.</param>
public sealed record SalesTextFilter(string Value, SalesTextOperator Operator);

/// <summary>
/// A single server-side numeric column filter (value + comparison).
/// </summary>
/// <param name="Operator">The comparison to apply.</param>
/// <param name="Value">The comparison operand (a raw amount in the
/// sale's own currency — the filter is currency-blind by design: the
/// grid column filters on the underlying decimal).</param>
public sealed record SalesAmountFilter(SalesAmountOperator Operator, decimal Value);

/// <summary>
/// The complete set of server-side list filters + sort applied by
/// <see cref="AllSalesSpecification"/> and
/// <see cref="SaleByCustomerSpecification"/> (Round 4 — server-driven
/// paging).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY AN AGGREGATE RECORD</b>: the two specs previously grew via
/// ctor-parameter sprawl (status, from, to, …). Round 4 adds six more
/// knobs (three column filters, an amount filter, a sort key, a sort
/// direction); packing them into ONE aggregate keeps the spec ctor
/// surface stable and gives the query handler a single thing to pass
/// through. The aggregate is deliberately serializable-friendly (a
/// positional record) because <see cref="GetSalesPaginatedQuery"/>
/// carries (a projection of) it over the wire via Wolverine.
/// </para>
/// <para>
/// <b>NULL = NO FILTER</b>: every member is optional; null members add
/// no WHERE clause. The pre-Round-4 constructors chain through with an
/// all-null aggregate so existing callers are unaffected.
/// </para>
/// <para>
/// <b>NOT INCLUDED HERE</b>: status and the creation-date range — those
/// predate Round 4 and stay as dedicated spec ctor parameters; and the
/// legacy <c>SearchTerm</c> (a cross-column OR search) which also stays
/// a dedicated parameter for MobileSearch compatibility.
/// </para>
/// </remarks>
/// <param name="SaleNumberTerm">Optional free-text term for the
/// sale-number column (parsed into Year/Sequence/draft predicates by
/// <see cref="SaleNumberSearchParser"/>).</param>
/// <param name="CustomerName">Optional filter on the customer display name.</param>
/// <param name="CreatedByName">Optional filter on the creator display name.</param>
/// <param name="Total">Optional filter on the sale total amount.</param>
/// <param name="SortBy">Optional sort key; null = default (newest first).</param>
/// <param name="SortDescending">Sort direction for <paramref name="SortBy"/> (and for the default sort).</param>
public sealed record SalesListFilters(
    string? SaleNumberTerm,
    SalesTextFilter? CustomerName,
    SalesTextFilter? CreatedByName,
    SalesAmountFilter? Total,
    SalesSortBy? SortBy,
    bool SortDescending);
