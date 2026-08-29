using System.Globalization;
using TakOne.Application.Products.Queries.GetProductsPaginated;

namespace TakOne.WebUI.Services;

/// <summary>
/// Translates the Radzen grid's LoadDataArgs descriptors into the typed
/// products-list query fields (Round 6 — server-driven paging for the
/// AdminProducts grid). Mirrors <see cref="UsersGridStateTranslator"/> from
/// Round 5 and <see cref="SalesGridStateTranslator"/> from Round 4.
/// CONTRACT (pinned against Radzen 11.1.8 by DataGridLoadDataContractTests):
/// the grid re-raises LoadData with the COMPLETE active filter set on every
/// load — a cleared filter disappears from args.Filters rather than arriving
/// as a null-valued descriptor. Sorts arrive as structured SortDescriptors;
/// filters as FilterDescriptors (property + raw value + operator).
/// LENIENT BY DESIGN: unknown properties, unparseable values, and
/// untranslatable operators are skipped, never thrown.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE TWO StockQuantity COLUMNS</b>: the grid has BOTH a Stock column
/// (Property="StockQuantity", Radzen's built-in numeric filter) and a Status
/// column (Property="StockQuantity", custom dropdown filter template) —
/// they emit descriptors under the SAME property name. They are told apart
/// by the VALUE's runtime shape: the status dropdown writes its
/// culture-neutral string key ("InStock"/"OutOfStock", mirroring the Sales
/// page's status pattern of passing the key as the filter value), while the
/// built-in numeric filter writes a number. Both descriptors can be active
/// at once and translate to SEPARATE query members that AND in SQL.
/// </para>
/// <para>
/// <b>CATEGORY COLUMNS</b>: CategoryName / SubCategoryName /
/// SubSubCategoryName descriptors pass through as text filters — the query
/// HANDLER resolves them against the category tree into Id sets (the
/// product row stores only Guid FKs; see
/// <see cref="ProductsListFilters.CategoryIds"/>). Their SORTS are
/// deliberately untranslatable here (cross-aggregate; see the
/// ProductSortBy doc) — a stray sort descriptor degrades to the default
/// order.
/// </para>
/// </remarks>
public static class ProductsGridStateTranslator
{
    /// <summary>
    /// The per-column filter bundle extracted from one LoadData event —
    /// mirrors <see cref="UsersGridStateTranslator.ColumnFilters"/>.
    /// </summary>
    public sealed record ColumnFilters(
        ProductsTextFilter? Name,
        ProductStockStatus? StockStatus,
        ProductsNumberFilter? Price,
        ProductsNumberFilter? Stock,
        ProductsTextFilter? CategoryName,
        ProductsTextFilter? SubCategoryName,
        ProductsTextFilter? SubSubCategoryName);

    /// <summary>
    /// Translates the grid's sort descriptors into the typed sort key +
    /// direction. Nulls (no user sort active) map to the repository's
    /// Name-ascending default — the pre-Round-4 order the shop relies on
    /// (NOT a descending default like the sales list's newest-first).
    /// </summary>
    public static (ProductSortBy? SortBy, bool SortDescending) TranslateSort(
        IEnumerable<Radzen.SortDescriptor>? sorts)
    {
        var sort = sorts?.FirstOrDefault();
        if (sort is null || string.IsNullOrEmpty(sort.Property))
            return (null, false); // no user sort → repo default (Name asc)

        var sortBy = sort.Property switch
        {
            "Name" => ProductSortBy.Name,
            // The price column's Property is the complex-property path
            // "Price.Amount"; its two directions map to the Round-4
            // direction-encoded keys.
            "Price.Amount" => ProductSortBy.PriceLowToHigh,
            "StockQuantity" => ProductSortBy.StockLowToHigh,
            // "CategoryName"/"SubCategoryName"/"SubSubCategoryName" are
            // deliberately absent: those names live on the Category
            // aggregate, so a sort would need a cross-table join — the
            // columns' header sorts are disabled in the UI and an
            // unexpected descriptor is skipped here.
            _ => (ProductSortBy?)null
        };
        if (sortBy is null) return (null, false);

        var descending = sort.SortOrder != Radzen.SortOrder.Ascending;
        return (sortBy, descending);
    }

    /// <summary>
    /// Translates the grid's filter descriptors into the typed column
    /// filters. A descriptor whose FilterValue is null (AllowClear'd) is
    /// skipped — the cleared filter simply disappears from the set.
    /// </summary>
    public static ColumnFilters TranslateFilters(
        IEnumerable<Radzen.FilterDescriptor>? filters)
    {
        var result = new ColumnFilters(null, null, null, null, null, null, null);
        foreach (var descriptor in filters ?? Enumerable.Empty<Radzen.FilterDescriptor>())
        {
            if (descriptor.FilterValue is null) continue; // AllowClear'd descriptor

            switch (descriptor.Property)
            {
                case "Name":
                    result = result with { Name = TranslateTextFilter(descriptor) };
                    break;

                case "Price.Amount":
                    result = result with { Price = TranslateNumberFilter(descriptor) };
                    break;

                case "StockQuantity":
                    // Two columns share this property — see the class
                    // remarks. The status dropdown's key arrives as a
                    // STRING; the stock column's built-in filter arrives as
                    // a number. Both are valid simultaneously and land in
                    // different query members.
                    if (descriptor.FilterValue is string statusKey)
                    {
                        result = result with { StockStatus = TranslateStockStatus(statusKey) };
                    }
                    else
                    {
                        result = result with { Stock = TranslateNumberFilter(descriptor) };
                    }
                    break;

                case "CategoryName":
                    result = result with { CategoryName = TranslateTextFilter(descriptor) };
                    break;

                case "SubCategoryName":
                    result = result with { SubCategoryName = TranslateTextFilter(descriptor) };
                    break;

                case "SubSubCategoryName":
                    result = result with { SubSubCategoryName = TranslateTextFilter(descriptor) };
                    break;

                // Unknown property: skipped (lenient).
            }
        }
        return result;
    }

    private static ProductsTextFilter? TranslateTextFilter(Radzen.FilterDescriptor descriptor)
    {
        if (descriptor.FilterValue is null || !IsTextishOperator(descriptor.FilterOperator))
            return null;
        var value = descriptor.FilterValue.ToString();
        if (string.IsNullOrWhiteSpace(value)) return null;
        var op = descriptor.FilterOperator switch
        {
            Radzen.FilterOperator.Contains => ProductsTextOperator.Contains,
            Radzen.FilterOperator.DoesNotContain => ProductsTextOperator.NotContains,
            Radzen.FilterOperator.Equals => ProductsTextOperator.Equals,
            Radzen.FilterOperator.NotEquals => ProductsTextOperator.NotEquals,
            Radzen.FilterOperator.StartsWith => ProductsTextOperator.StartsWith,
            Radzen.FilterOperator.EndsWith => ProductsTextOperator.EndsWith,
            _ => (ProductsTextOperator?)null
        };
        return op.HasValue ? new ProductsTextFilter(value, op.Value) : null;
    }

    private static bool IsTextishOperator(Radzen.FilterOperator op) =>
        op is Radzen.FilterOperator.Contains or Radzen.FilterOperator.DoesNotContain or
           Radzen.FilterOperator.Equals or Radzen.FilterOperator.NotEquals or
           Radzen.FilterOperator.StartsWith or Radzen.FilterOperator.EndsWith;

    /// <summary>
    /// The Price / Stock columns' built-in numeric filters emit one of the
    /// six comparison operators with a numeric value (int for the stock
    /// column, decimal for the price column). The typed record stores a
    /// decimal so one shape serves both. String values are converted
    /// invariantly as defense (a deserialized filter state could carry the
    /// number as text); unconvertible ones are skipped (lenient) — the
    /// same conversion contract as the sales list's amount filter.
    /// </summary>
    private static ProductsNumberFilter? TranslateNumberFilter(Radzen.FilterDescriptor descriptor)
    {
        if (descriptor.FilterValue is null ||
            !IsNumberishOperator(descriptor.FilterOperator) ||
            !TryConvertToDecimal(descriptor.FilterValue, out var value))
        {
            return null;
        }

        var op = descriptor.FilterOperator switch
        {
            Radzen.FilterOperator.Equals => ProductsNumberOperator.Equals,
            Radzen.FilterOperator.NotEquals => ProductsNumberOperator.NotEquals,
            Radzen.FilterOperator.GreaterThan => ProductsNumberOperator.GreaterThan,
            Radzen.FilterOperator.GreaterThanOrEquals => ProductsNumberOperator.GreaterThanOrEqual,
            Radzen.FilterOperator.LessThan => ProductsNumberOperator.LessThan,
            Radzen.FilterOperator.LessThanOrEquals => ProductsNumberOperator.LessThanOrEqual,
            _ => (ProductsNumberOperator?)null
        };
        return op.HasValue ? new ProductsNumberFilter(op.Value, value) : null;
    }

    private static bool IsNumberishOperator(Radzen.FilterOperator op) =>
        op is Radzen.FilterOperator.Equals or Radzen.FilterOperator.NotEquals or
           Radzen.FilterOperator.GreaterThan or Radzen.FilterOperator.GreaterThanOrEquals or
           Radzen.FilterOperator.LessThan or Radzen.FilterOperator.LessThanOrEquals;

    private static bool TryConvertToDecimal(object value, out decimal amount)
    {
        try
        {
            // Radzen parses the typed input into the numeric CLR type of
            // the column's property — decimal for Price.Amount, but
            // int/long/double are possible (the Stock column is an int);
            // invariant conversion handles them all without culture
            // surprises. A non-numeric string (stale filter state) throws
            // FormatException; an unrelated CLR type throws
            // InvalidCastException — both degrade to "untranslatable".
            amount = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
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

    /// <summary>
    /// The Status column's FilterTemplate dropdown writes its
    /// culture-neutral string key ("InStock"/"OutOfStock") into the
    /// column's FilterValue (mirroring the Sales page's status pattern —
    /// the key travels as the raw descriptor payload). Unknown strings
    /// (e.g. a stale value from an older filter shape) are skipped
    /// (lenient).
    /// </summary>
    private static ProductStockStatus? TranslateStockStatus(string? value)
    {
        return value switch
        {
            "InStock" => ProductStockStatus.InStock,
            "OutOfStock" => ProductStockStatus.OutOfStock,
            _ => null
        };
    }
}
