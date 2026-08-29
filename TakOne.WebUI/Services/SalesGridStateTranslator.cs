using TakOne.Application.Sales.Queries.GetSalesPaginated;
using TakOne.Application.Sales.Specifications;
using TakOne.Domain.Sales.Enums;

namespace TakOne.WebUI.Services;

/// <summary>
/// Translates the Radzen grid's <c>LoadDataArgs</c> descriptors into the
/// typed sales-list query fields (Round 4 — server-driven paging).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY A SEPARATE CLASS</b>: the translation is pure, deterministic
/// logic — exactly the kind of thing the WebUI test suite can cover
/// without wiring a full-page bUnit context (IMessageBus, auth state,
/// localizer, …). The Sales page keeps a thin LoadData handler; every
/// mapping decision lives here, unit-tested in
/// <c>SalesGridStateTranslatorTests</c>.
/// </para>
/// <para>
/// <b>CONTRACT</b> (pinned against Radzen 11.1.8 by
/// <c>DataGridLoadDataContractTests</c>): the grid re-raises LoadData
/// with the COMPLETE active filter set on every load — a cleared filter
/// disappears from <c>args.Filters</c> rather than arriving as a
/// null-valued descriptor. Sorts arrive as structured
/// <see cref="Radzen.SortDescriptor"/>s (property + order); filters as
/// <see cref="Radzen.FilterDescriptor"/>s (property + raw value +
/// operator).
/// </para>
/// <para>
/// <b>LENIENT BY DESIGN</b>: unknown properties, unparseable values, and
/// untranslatable operators are skipped, never thrown — the grid is one
/// Radzen version bump away from emitting a new descriptor shape, and a
/// dropped filter beats a dead page. (The one exception is deliberate:
/// an unparseable SALE-NUMBER term still travels to the server, where
/// <see cref="SaleNumberSearchParser"/> maps it to a match-nothing
/// predicate — the user typed a filter, so silently ignoring it would
/// be misleading.)
/// </para>
/// </remarks>
public static class SalesGridStateTranslator
{
    /// <summary>
    /// The translated column-filter state — one value per filterable
    /// column of the sales grid.
    /// </summary>
    public sealed record ColumnFilters(
        string? SaleNumberTerm,
        SalesTextFilter? CustomerName,
        SalesTextFilter? CreatedByName,
        SalesAmountFilter? Total,
        SaleStatus? Status);

    /// <summary>
    /// Maps the grid's structured sort descriptors onto the typed sort
    /// key + direction. The FIRST descriptor wins — the grid's sort mode
    /// is single-key by default; the spec's Id tiebreaker keeps even
    /// single-key sorts fully deterministic. No descriptors = no user
    /// sort (the spec defaults to newest-first).
    /// </summary>
    public static (SalesSortBy? SortBy, bool SortDescending) TranslateSort(
        IEnumerable<Radzen.SortDescriptor>? sorts)
    {
        var sort = sorts?.FirstOrDefault();
        if (sort is null || string.IsNullOrEmpty(sort.Property))
        {
            return (null, true); // no user sort → spec default (newest first)
        }

        var sortBy = sort.Property switch
        {
            // The sale-number column binds to the DTO's computed
            // DisplayNumber property; the server sorts by the number's
            // integer parts.
            "DisplayNumber" => SalesSortBy.SaleNumber,
            "Status" => SalesSortBy.Status,
            "Total.Amount" => SalesSortBy.Total,
            "CustomerName" => SalesSortBy.CustomerName,
            "CreatedByName" => SalesSortBy.CreatedByName,
            "CreatedAtUtc" => SalesSortBy.CreatedAtUtc,
            _ => (SalesSortBy?)null
        };

        // Unknown property → no sort key → the spec's newest-first
        // default (fully descending), regardless of what the descriptor
        // claimed.
        if (sortBy is null)
        {
            return (null, true);
        }

        var descending = sort.SortOrder != Radzen.SortOrder.Ascending;
        return (sortBy, descending);
    }

    /// <summary>
    /// Maps the grid's filter descriptors onto the typed query filters.
    /// Assumes a FRESH result every call (the descriptor collection is
    /// the complete active set — see the class remarks).
    /// </summary>
    public static ColumnFilters TranslateFilters(
        IEnumerable<Radzen.FilterDescriptor>? filters)
    {
        var result = new ColumnFilters(null, null, null, null, null);

        foreach (var descriptor in filters ?? Enumerable.Empty<Radzen.FilterDescriptor>())
        {
            if (descriptor.FilterValue is null)
            {
                // AllowClear'd filters may still emit a null-valued
                // descriptor; there is nothing to translate.
                continue;
            }

            switch (descriptor.Property)
            {
                case "DisplayNumber":
                    if (IsTextishOperator(descriptor.FilterOperator))
                    {
                        result = result with { SaleNumberTerm = descriptor.FilterValue.ToString() };
                    }
                    break;

                case "Status":
                    if (Enum.TryParse<SaleStatus>(
                            descriptor.FilterValue.ToString(),
                            ignoreCase: true,
                            out var status))
                    {
                        result = result with { Status = status };
                    }
                    break;

                case "Total.Amount":
                    result = result with { Total = TranslateAmountFilter(descriptor) };
                    break;

                case "CustomerName":
                    result = result with { CustomerName = TranslateTextFilter(descriptor) };
                    break;

                case "CreatedByName":
                    result = result with { CreatedByName = TranslateTextFilter(descriptor) };
                    break;

                // Unknown property: skipped (lenient — see class remarks).
            }
        }

        return result;
    }

    private static bool IsTextishOperator(Radzen.FilterOperator op) =>
        op is Radzen.FilterOperator.Contains or Radzen.FilterOperator.DoesNotContain or
           Radzen.FilterOperator.Equals or Radzen.FilterOperator.NotEquals or
           Radzen.FilterOperator.StartsWith or Radzen.FilterOperator.EndsWith;

    /// <summary>
    /// Maps a Radzen text filter descriptor onto the typed
    /// <see cref="SalesTextFilter"/> (null when the operator/value can't
    /// be expressed).
    /// </summary>
    private static SalesTextFilter? TranslateTextFilter(Radzen.FilterDescriptor descriptor)
    {
        if (descriptor.FilterValue is null ||
            !IsTextishOperator(descriptor.FilterOperator))
        {
            return null;
        }

        var value = descriptor.FilterValue.ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var op = descriptor.FilterOperator switch
        {
            Radzen.FilterOperator.Contains => SalesTextOperator.Contains,
            Radzen.FilterOperator.DoesNotContain => SalesTextOperator.NotContains,
            Radzen.FilterOperator.Equals => SalesTextOperator.Equals,
            Radzen.FilterOperator.NotEquals => SalesTextOperator.NotEquals,
            Radzen.FilterOperator.StartsWith => SalesTextOperator.StartsWith,
            Radzen.FilterOperator.EndsWith => SalesTextOperator.EndsWith,
            _ => (SalesTextOperator?)null
        };

        return op.HasValue ? new SalesTextFilter(value, op.Value) : null;
    }

    /// <summary>
    /// Maps a Radzen numeric filter descriptor onto the typed
    /// <see cref="SalesAmountFilter"/> (null when the operator/value
    /// can't be expressed — Radzen's numeric menus emit the six
    /// comparison operators; anything else is skipped, not thrown).
    /// </summary>
    private static SalesAmountFilter? TranslateAmountFilter(Radzen.FilterDescriptor descriptor)
    {
        if (descriptor.FilterValue is null ||
            !TryConvertToDecimal(descriptor.FilterValue, out var amount))
        {
            return null;
        }

        var op = descriptor.FilterOperator switch
        {
            Radzen.FilterOperator.Equals => SalesAmountOperator.Equals,
            Radzen.FilterOperator.NotEquals => SalesAmountOperator.NotEquals,
            Radzen.FilterOperator.GreaterThan => SalesAmountOperator.GreaterThan,
            Radzen.FilterOperator.GreaterThanOrEquals => SalesAmountOperator.GreaterThanOrEqual,
            Radzen.FilterOperator.LessThan => SalesAmountOperator.LessThan,
            Radzen.FilterOperator.LessThanOrEquals => SalesAmountOperator.LessThanOrEqual,
            _ => (SalesAmountOperator?)null
        };

        return op.HasValue ? new SalesAmountFilter(op.Value, amount) : null;
    }

    private static bool TryConvertToDecimal(object value, out decimal amount)
    {
        try
        {
            // Radzen parses the typed input into the numeric CLR type of
            // the column's property — decimal for Total.Amount, but
            // int/long/double are possible; invariant conversion handles
            // them all without culture surprises.
            amount = Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch (FormatException)
        {
            amount = 0m;
            return false;
        }
        catch (InvalidCastException)
        {
            amount = 0m;
            return false;
        }
    }
}
