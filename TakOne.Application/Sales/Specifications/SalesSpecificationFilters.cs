using System.Linq.Expressions;
using System.Reflection;
using Ardalis.Specification;
using TakOne.Application.Sales.Queries.GetSalesPaginated;
using TakOne.Domain.Sales.Entities;

namespace TakOne.Application.Sales.Specifications;

/// <summary>
/// Shared WHERE/ORDER-BY clauses for the sales list specifications
/// (Round 4 — server-driven paging). Extracted from
/// <see cref="AllSalesSpecification"/> and
/// <see cref="SaleByCustomerSpecification"/> so both specs apply the
/// IDENTICAL filter + sort semantics (they previously duplicated their
/// date-range blocks — this class is the single home for the shared
/// logic, including that block's successor).
/// </summary>
/// <remarks>
/// <para>
/// <b>EF TRANSLATABILITY CONTRACT</b>: every clause below must
/// translate to SQL on both SQL Server (production) and SQLite
/// (integration tests). That rules out:
/// <list type="bullet">
///   <item><c>Contains</c> on <see cref="Domain.Sales.ValueObjects.SaleNumber.Value"/>
///   — computed-on-access, unmapped (see
///   <see cref="SaleNumberSearchParser"/>).</item>
///   <item>Culture-aware string comparison — instead both sides are
///   lowered with plain <c>ToLower()</c>, which translates to
///   <c>LOWER()</c> on both providers. Persian text is caseless, and
///   SQLite's ASCII-only <c>LOWER</c> is irrelevant for it; Latin
///   names match case-insensitively on both providers. (The
///   culture-taking overloads are NOT EF-translatable, hence the
///   CA1304/CA1311/CA1862 suppressions below.)</item>
/// </list>
/// The SQLite integration tests (<c>SalesListFilteringIntegrationTests</c>)
/// execute every clause against a real EF provider to keep this
/// contract honest.
/// </para>
/// <para>
/// <b>CLOSURE NOTES</b>: the lambdas capture trimmed/lowered locals
/// (e.g. <c>nameTerm</c>) so EF parameterizes the constants. The
/// <c>year != null</c>-style guards inside the search-term lambda are
/// fully-evaluable subtrees (no lambda-parameter reference), so EF's
/// funcletizer evaluates them client-side — they compile away in SQL
/// and keep the in-memory unit-test evaluation correct.
/// </para>
/// </remarks>
internal static class SalesSpecificationFilters
{
    // The expression-tree string predicates below use the parameterless
    // string.ToLower()/Contains(string) deliberately: those are the
    // overloads EF Core translates to LOWER()/LIKE. The culture-taking
    // overloads the analyzers prefer are NOT translatable. See the class
    // remarks ("EF translatability contract").
#pragma warning disable CA1304 // ToLower culture — SQL LOWER() has no culture
#pragma warning disable CA1311 // Contains culture — same
#pragma warning disable CA1862 // OrdinalIgnoreCase overload — not EF-translatable

    /// <summary>
    /// Applies the legacy cross-column <c>SearchTerm</c> (MobileSearch
    /// compatibility): number-match OR customer-name-match, as ONE OR
    /// predicate. Empty term = no clause.
    /// </summary>
    public static void ApplySearchTerm(
        this ISpecificationBuilder<Sale> query, string? searchTerm)
    {
        var term = searchTerm?.Trim();
        if (string.IsNullOrEmpty(term))
        {
            return;
        }

        var parsed = SaleNumberSearchParser.Parse(term);
        var nameTerm = term.ToLowerInvariant();

        // Pre-evaluated shape flags (see class remarks on funcletization).
        var year = parsed.Year;
        var sequence = parsed.Sequence;
        var sequenceOrYear = parsed.SequenceOrYear;
        var draftsOnly = parsed.DraftsOnly;
        var numberedOnly = parsed.NumberedOnly;

        // NOTE: a garbage term (MatchNothing) is intentionally NOT an
        // explicit arm here — with every shape flag false and the name
        // arm not matching, the whole OR is false, i.e. a garbage
        // search term matches NOTHING (the honest behavior for a
        // cross-column search; the column-filter path in
        // ApplySaleNumberTerm uses Where(false) for the same outcome).
        //
        // ARM SHAPES (the parser returns exactly ONE number shape, so
        // the guards are mutually exclusive): a FULL number
        // (year+sequence) requires BOTH parts to match — the year arm
        // alone would over-match every sale of that year.
        query.Where(sale =>
            sale.CustomerName.ToLower().Contains(nameTerm) ||
            (year != null && sequence != null && sale.SaleNumber != null &&
                sale.SaleNumber.Year == year && sale.SaleNumber.Sequence == sequence) ||
            (year != null && sequence == null && sale.SaleNumber != null &&
                sale.SaleNumber.Year == year) ||
            (sequenceOrYear != null && sale.SaleNumber != null &&
                (sale.SaleNumber.Sequence == sequenceOrYear || sale.SaleNumber.Year == sequenceOrYear)) ||
            (draftsOnly && sale.SaleNumber == null) ||
            (numberedOnly && sale.SaleNumber != null));
    }

    /// <summary>
    /// Applies the column-filter bundle from
    /// <see cref="SalesListFilters"/>: sale-number term, customer-name
    /// filter, creator-name filter, total-amount filter. Null members
    /// add no clause.
    /// </summary>
    public static void ApplyColumnFilters(
        this ISpecificationBuilder<Sale> query, SalesListFilters? filters)
    {
        if (filters is null)
        {
            return;
        }

        ApplySaleNumberTerm(query, filters.SaleNumberTerm);
        ApplyTextFilter(query, filters.CustomerName, sale => sale.CustomerName);
        ApplyTextFilter(query, filters.CreatedByName, sale => sale.CreatedByName);
        ApplyAmountFilter(query, filters.Total);
    }

    /// <summary>
    /// Applies the sort: user-selected key + direction, with the
    /// pre-Round-4 default (CreatedAtUtc DESC) preserved when no sort
    /// is active, and the sale <c>Id</c> as a deterministic tiebreaker
    /// so OFFSET/FETCH paging can never skip or duplicate rows.
    /// </summary>
    public static void ApplySort(
        this ISpecificationBuilder<Sale> query,
        SalesSortBy? sortBy,
        bool descending)
    {
        switch (sortBy ?? SalesSortBy.CreatedAtUtc)
        {
            case SalesSortBy.SaleNumber:
                // The display string is unmapped; sort by the integer
                // parts. Drafts (NULL number) sort as a block; the Id
                // tiebreaker keeps them deterministic within it.
                if (descending)
                {
                    query.OrderByDescending(sale => sale.SaleNumber!.Year)
                         .ThenByDescending(sale => sale.SaleNumber!.Sequence)
                         .ThenByDescending(sale => sale.Id);
                }
                else
                {
                    query.OrderBy(sale => sale.SaleNumber!.Year)
                         .ThenBy(sale => sale.SaleNumber!.Sequence)
                         .ThenBy(sale => sale.Id);
                }
                break;

            case SalesSortBy.Status:
                if (descending)
                {
                    query.OrderByDescending(sale => sale.Status)
                         .ThenByDescending(sale => sale.Id);
                }
                else
                {
                    query.OrderBy(sale => sale.Status)
                         .ThenBy(sale => sale.Id);
                }
                break;

            case SalesSortBy.Total:
                if (descending)
                {
                    query.OrderByDescending(sale => sale.Total.Amount)
                         .ThenByDescending(sale => sale.Id);
                }
                else
                {
                    query.OrderBy(sale => sale.Total.Amount)
                         .ThenBy(sale => sale.Id);
                }
                break;

            case SalesSortBy.CustomerName:
                if (descending)
                {
                    query.OrderByDescending(sale => sale.CustomerName)
                         .ThenByDescending(sale => sale.Id);
                }
                else
                {
                    query.OrderBy(sale => sale.CustomerName)
                         .ThenBy(sale => sale.Id);
                }
                break;

            case SalesSortBy.CreatedByName:
                if (descending)
                {
                    query.OrderByDescending(sale => sale.CreatedByName)
                         .ThenByDescending(sale => sale.Id);
                }
                else
                {
                    query.OrderBy(sale => sale.CreatedByName)
                         .ThenBy(sale => sale.Id);
                }
                break;

            case SalesSortBy.CreatedAtUtc:
            default:
                if (descending)
                {
                    query.OrderByDescending(sale => sale.CreatedAtUtc)
                         .ThenByDescending(sale => sale.Id);
                }
                else
                {
                    query.OrderBy(sale => sale.CreatedAtUtc)
                         .ThenBy(sale => sale.Id);
                }
                break;
        }
    }

#pragma warning restore CA1304
#pragma warning restore CA1311
#pragma warning restore CA1862

    // ── Internals ────────────────────────────────────────────────────

    private static void ApplySaleNumberTerm(
        ISpecificationBuilder<Sale> query, string? term)
    {
        var trimmed = term?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return;
        }

        var parsed = SaleNumberSearchParser.Parse(trimmed);
        var year = parsed.Year;
        var sequence = parsed.Sequence;
        var sequenceOrYear = parsed.SequenceOrYear;

        if (parsed.MatchNothing)
        {
            query.Where(sale => false);
            return;
        }

        if (parsed.DraftsOnly)
        {
            query.Where(sale => sale.SaleNumber == null);
            return;
        }

        if (parsed.NumberedOnly)
        {
            query.Where(sale => sale.SaleNumber != null);
            return;
        }

        if (year.HasValue && sequence.HasValue)
        {
            query.Where(sale => sale.SaleNumber != null &&
                                sale.SaleNumber.Year == year &&
                                sale.SaleNumber.Sequence == sequence);
            return;
        }

        if (year.HasValue)
        {
            query.Where(sale => sale.SaleNumber != null &&
                                sale.SaleNumber.Year == year);
            return;
        }

        if (sequenceOrYear.HasValue)
        {
            query.Where(sale => sale.SaleNumber != null &&
                                (sale.SaleNumber.Sequence == sequenceOrYear ||
                                 sale.SaleNumber.Year == sequenceOrYear));
        }
    }

    private static void ApplyTextFilter(
        ISpecificationBuilder<Sale> query,
        SalesTextFilter? filter,
        Expression<Func<Sale, string>> selector)
    {
        var term = filter?.Value?.Trim();
        if (filter is null || string.IsNullOrEmpty(term))
        {
            return;
        }

        var value = term.ToLowerInvariant();

        // The six operators dispatch on the SELECTED member. The
        // selector is a simple member expression (CustomerName /
        // CreatedByName), so rebuilding the body per operator is a
        // matter of swapping the string method call around the lowered
        // member. Built by hand (rather than via a captured sub-lambda)
        // so the final tree stays a plain MemberAccess → method-call
        // chain that EF translates to LOWER()/LIKE.
        var body = selector.Body;
        var sale = selector.Parameters[0];
        var lowered = Expression.Call(body, ToLowerMethod);

        Expression? predicate = filter.Operator switch
        {
            SalesTextOperator.Contains =>
                Expression.Call(lowered, ContainsMethod, Expression.Constant(value)),

            SalesTextOperator.NotContains =>
                Expression.Not(Expression.Call(
                    lowered, ContainsMethod, Expression.Constant(value))),

            SalesTextOperator.Equals =>
                Expression.Equal(lowered, Expression.Constant(value)),

            SalesTextOperator.NotEquals =>
                Expression.NotEqual(lowered, Expression.Constant(value)),

            SalesTextOperator.StartsWith =>
                Expression.Call(lowered, StartsWithMethod, Expression.Constant(value)),

            SalesTextOperator.EndsWith =>
                Expression.Call(lowered, EndsWithMethod, Expression.Constant(value)),

            // Unknown operator values (a malformed message could carry
            // an out-of-range enum) are ignored — lenient no-filter.
            _ => null
        };

        if (predicate is null)
        {
            return;
        }

        query.Where(Expression.Lambda<Func<Sale, bool>>(predicate, sale));
    }

    private static void ApplyAmountFilter(
        ISpecificationBuilder<Sale> query, SalesAmountFilter? filter)
    {
        if (filter is null)
        {
            return;
        }

        var value = filter.Value;
        switch (filter.Operator)
        {
            case SalesAmountOperator.Equals:
                query.Where(sale => sale.Total.Amount == value);
                break;
            case SalesAmountOperator.NotEquals:
                query.Where(sale => sale.Total.Amount != value);
                break;
            case SalesAmountOperator.GreaterThan:
                query.Where(sale => sale.Total.Amount > value);
                break;
            case SalesAmountOperator.GreaterThanOrEqual:
                query.Where(sale => sale.Total.Amount >= value);
                break;
            case SalesAmountOperator.LessThan:
                query.Where(sale => sale.Total.Amount < value);
                break;
            case SalesAmountOperator.LessThanOrEqual:
                query.Where(sale => sale.Total.Amount <= value);
                break;
        }
    }

    private static readonly MethodInfo ToLowerMethod =
        typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;

    private static readonly MethodInfo ContainsMethod =
        typeof(string).GetMethod(nameof(string.Contains), new[] { typeof(string) })!;

    private static readonly MethodInfo StartsWithMethod =
        typeof(string).GetMethod(nameof(string.StartsWith), new[] { typeof(string) })!;

    private static readonly MethodInfo EndsWithMethod =
        typeof(string).GetMethod(nameof(string.EndsWith), new[] { typeof(string) })!;
}
