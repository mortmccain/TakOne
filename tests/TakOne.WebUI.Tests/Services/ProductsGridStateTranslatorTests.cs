using FluentAssertions;
using Radzen;
using TakOne.Application.Products.Queries.GetProductsPaginated;
using TakOne.WebUI.Services;
using Xunit;

namespace TakOne.WebUI.Tests.Services;

/// <summary>
/// Unit tests for <see cref="ProductsGridStateTranslator"/> — the Radzen
/// LoadData descriptor → typed query translation used by the AdminProducts
/// grid (Round 6 — server-driven paging). Mirrors
/// <see cref="UsersGridStateTranslatorTests"/> from Round 5.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THIS LAYER</b>: the translation is the only non-trivial logic
/// in the AdminProducts page's LoadData path. Keeping it in a pure static
/// class lets these tests pin every mapping decision — column property
/// names (including the complex-property path "Price.Amount" and the TWO
/// columns that share "StockQuantity"), operator enums, value conversions
/// (the status dropdown's string keys vs the stock column's numeric
/// values), and the lenient-skip behavior for unknown shapes — without a
/// full-page bUnit context.
/// </para>
/// <para>
/// The Radzen side of the contract (that the grid actually DELIVERS these
/// descriptor shapes in LoadData mode) is pinned separately by
/// <c>DataGridLoadDataContractTests</c> in the ComponentTests project.
/// </para>
/// </remarks>
public class ProductsGridStateTranslatorTests
{
    // ── Sort translation ─────────────────────────────────────────────

    [Fact]
    public void TranslateSort_NoSorts_DefaultsToNameAscending()
    {
        var (sortBy, descending) = ProductsGridStateTranslator.TranslateSort(null);

        sortBy.Should().BeNull("no user sort active");
        descending.Should().BeFalse(
            "the products-list default is Name ASCENDING (the pre-Round-4 order " +
            "the shop relies on) — NOT the sales list's newest-first");
    }

    [Fact]
    public void TranslateSort_EmptySorts_DefaultsToNameAscending()
    {
        var (sortBy, descending) = ProductsGridStateTranslator.TranslateSort(
            Array.Empty<SortDescriptor>());

        sortBy.Should().BeNull();
        descending.Should().BeFalse();
    }

    [Theory]
    [InlineData("Name", ProductSortBy.Name)]
    [InlineData("Price.Amount", ProductSortBy.PriceLowToHigh)]
    [InlineData("StockQuantity", ProductSortBy.StockLowToHigh)]
    public void TranslateSort_KnownColumns_MapToSortKeys(
        string property, ProductSortBy expected)
    {
        var (sortBy, _) = ProductsGridStateTranslator.TranslateSort(
            new[] { new SortDescriptor { Property = property, SortOrder = SortOrder.Ascending } });

        sortBy.Should().Be(expected);
    }

    [Theory]
    [InlineData("CategoryName")]
    [InlineData("SubCategoryName")]
    [InlineData("SubSubCategoryName")]
    public void TranslateSort_CategoryColumns_YieldNoSortKey(string property)
    {
        // The three category columns are deliberately unsortable in the
        // UI (the names live on the Category aggregate — a sort would
        // need a cross-table LEFT JOIN), but a stale/deserialized
        // descriptor must degrade to the default sort, not throw.
        var (sortBy, _) = ProductsGridStateTranslator.TranslateSort(
            new[] { new SortDescriptor { Property = property, SortOrder = SortOrder.Ascending } });

        sortBy.Should().BeNull("category names have no server-side sort key (cross-aggregate)");
    }

    [Fact]
    public void TranslateSort_DescendingOrder_SetsDescendingFlag()
    {
        var (sortBy, descending) = ProductsGridStateTranslator.TranslateSort(
            new[] { new SortDescriptor { Property = "Name", SortOrder = SortOrder.Descending } });

        sortBy.Should().Be(ProductSortBy.Name);
        descending.Should().BeTrue();
    }

    [Fact]
    public void TranslateSort_UnknownProperty_YieldsNoSortKey()
    {
        var (sortBy, _) = ProductsGridStateTranslator.TranslateSort(
            new[] { new SortDescriptor { Property = "SomeNewColumn", SortOrder = SortOrder.Ascending } });

        sortBy.Should().BeNull("unknown properties fall back to the default sort");
    }

    [Fact]
    public void TranslateSort_SecondaryDescriptors_AreIgnored()
    {
        var sorts = new[]
        {
            new SortDescriptor { Property = "Price.Amount", SortOrder = SortOrder.Ascending },
            new SortDescriptor { Property = "Name", SortOrder = SortOrder.Descending }
        };

        var (sortBy, descending) = ProductsGridStateTranslator.TranslateSort(sorts);

        sortBy.Should().Be(ProductSortBy.PriceLowToHigh);
        descending.Should().BeFalse();
    }

    // ── Filter translation ───────────────────────────────────────────

    [Fact]
    public void TranslateFilters_NullFilters_EverythingClear()
    {
        var result = ProductsGridStateTranslator.TranslateFilters(null);

        result.Name.Should().BeNull();
        result.StockStatus.Should().BeNull();
        result.Price.Should().BeNull();
        result.Stock.Should().BeNull();
        result.CategoryName.Should().BeNull();
        result.SubCategoryName.Should().BeNull();
        result.SubSubCategoryName.Should().BeNull();
    }

    [Fact]
    public void TranslateFilters_NullValuedDescriptor_Skipped()
    {
        // An AllowClear'd dropdown clears its descriptor's value — the
        // filter simply disappears from the set (pinned Radzen contract).
        var filters = new[]
        {
            new FilterDescriptor { Property = "StockQuantity", FilterValue = null }
        };

        var result = ProductsGridStateTranslator.TranslateFilters(filters);

        result.StockStatus.Should().BeNull();
        result.Stock.Should().BeNull();
    }

    [Fact]
    public void TranslateFilters_CompleteSet_MapsEveryColumn()
    {
        var filters = new[]
        {
            new FilterDescriptor
            {
                Property = "Name", FilterValue = "widget", FilterOperator = FilterOperator.Contains
            },
            new FilterDescriptor
            {
                Property = "Price.Amount", FilterValue = 250m, FilterOperator = FilterOperator.LessThanOrEquals
            },
            new FilterDescriptor
            {
                Property = "StockQuantity", FilterValue = 0, FilterOperator = FilterOperator.Equals
            },
            new FilterDescriptor
            {
                Property = "CategoryName", FilterValue = "elec", FilterOperator = FilterOperator.StartsWith
            },
            new FilterDescriptor
            {
                Property = "SubCategoryName", FilterValue = "laptops", FilterOperator = FilterOperator.Contains
            },
            new FilterDescriptor
            {
                Property = "SubSubCategoryName", FilterValue = "gaming", FilterOperator = FilterOperator.EndsWith
            }
        };

        var result = ProductsGridStateTranslator.TranslateFilters(filters);

        result.Name.Should().Be(new ProductsTextFilter("widget", ProductsTextOperator.Contains));
        result.Price.Should().Be(new ProductsNumberFilter(ProductsNumberOperator.LessThanOrEqual, 250m));
        result.Stock.Should().Be(new ProductsNumberFilter(ProductsNumberOperator.Equals, 0m));
        result.CategoryName.Should().Be(new ProductsTextFilter("elec", ProductsTextOperator.StartsWith));
        result.SubCategoryName.Should().Be(new ProductsTextFilter("laptops", ProductsTextOperator.Contains));
        result.SubSubCategoryName.Should().Be(new ProductsTextFilter("gaming", ProductsTextOperator.EndsWith));
    }

    [Theory]
    [InlineData(FilterOperator.Contains, ProductsTextOperator.Contains)]
    [InlineData(FilterOperator.DoesNotContain, ProductsTextOperator.NotContains)]
    [InlineData(FilterOperator.Equals, ProductsTextOperator.Equals)]
    [InlineData(FilterOperator.NotEquals, ProductsTextOperator.NotEquals)]
    [InlineData(FilterOperator.StartsWith, ProductsTextOperator.StartsWith)]
    [InlineData(FilterOperator.EndsWith, ProductsTextOperator.EndsWith)]
    public void TranslateFilters_TextOperators_MapOneToOne(
        FilterOperator radzenOperator, ProductsTextOperator expected)
    {
        var filters = new[]
        {
            new FilterDescriptor
            {
                Property = "Name", FilterValue = "x", FilterOperator = radzenOperator
            }
        };

        var result = ProductsGridStateTranslator.TranslateFilters(filters);

        result.Name.Should().Be(new ProductsTextFilter("x", expected));
    }

    [Fact]
    public void TranslateFilters_NonTextOperatorOnTextColumn_Skipped()
    {
        // The filter MENU can offer numeric operators (GreaterThan, …) on
        // a text column — untranslatable, so skipped (lenient).
        var filters = new[]
        {
            new FilterDescriptor
            {
                Property = "Name", FilterValue = "x", FilterOperator = FilterOperator.GreaterThan
            }
        };

        var result = ProductsGridStateTranslator.TranslateFilters(filters);

        result.Name.Should().BeNull();
    }

    [Fact]
    public void TranslateFilters_WhitespaceTextValue_Skipped()
    {
        var filters = new[]
        {
            new FilterDescriptor
            {
                Property = "Name", FilterValue = "   ", FilterOperator = FilterOperator.Contains
            }
        };

        var result = ProductsGridStateTranslator.TranslateFilters(filters);

        result.Name.Should().BeNull();
    }

    // ── The Status dropdown's string-key form vs the Stock column's
    //    numeric form (both arrive under Property="StockQuantity") ────

    [Theory]
    [InlineData("InStock", ProductStockStatus.InStock)]
    [InlineData("OutOfStock", ProductStockStatus.OutOfStock)]
    public void TranslateFilters_StatusStringKey_MapsToStockStatus(
        string key, ProductStockStatus expected)
    {
        // The status dropdown writes its culture-neutral key as the
        // descriptor payload (mirroring the Sales page's pattern).
        var filters = new[]
        {
            new FilterDescriptor
            {
                Property = "StockQuantity", FilterValue = key, FilterOperator = FilterOperator.Equals
            }
        };

        var result = ProductsGridStateTranslator.TranslateFilters(filters);

        result.StockStatus.Should().Be(expected);
        result.Stock.Should().BeNull("the string form never lands in the numeric filter");
    }

    [Fact]
    public void TranslateFilters_UnknownStatusString_Skipped()
    {
        var filters = new[]
        {
            new FilterDescriptor
            {
                Property = "StockQuantity", FilterValue = "Maybe", FilterOperator = FilterOperator.Equals
            }
        };

        var result = ProductsGridStateTranslator.TranslateFilters(filters);

        result.StockStatus.Should().BeNull();
        result.Stock.Should().BeNull();
    }

    [Fact]
    public void TranslateFilters_StockNumericValue_MapsToStockNumberFilter()
    {
        // The Stock column's built-in numeric filter writes a NUMBER —
        // that's the numeric filter, even for the value 0 (the reason the
        // pre-Round-6 page had to dance around Radzen's built-in 0
        // sentinel: that sentinel only ever applied to the built-in
        // filter path, which the status dropdown no longer uses).
        var filters = new[]
        {
            new FilterDescriptor
            {
                Property = "StockQuantity", FilterValue = 0, FilterOperator = FilterOperator.Equals
            }
        };

        var result = ProductsGridStateTranslator.TranslateFilters(filters);

        result.Stock.Should().Be(new ProductsNumberFilter(ProductsNumberOperator.Equals, 0m));
        result.StockStatus.Should().BeNull("the numeric form never lands in the status filter");
    }

    [Fact]
    public void TranslateFilters_StatusAndStockDescriptors_Coexist()
    {
        // Both StockQuantity columns can carry a filter at once — the
        // dropdown's string key AND the numeric column filter translate
        // to SEPARATE members that AND in SQL.
        var filters = new[]
        {
            new FilterDescriptor
            {
                Property = "StockQuantity", FilterValue = "OutOfStock", FilterOperator = FilterOperator.Equals
            },
            new FilterDescriptor
            {
                Property = "StockQuantity", FilterValue = 0, FilterOperator = FilterOperator.LessThanOrEquals
            }
        };

        var result = ProductsGridStateTranslator.TranslateFilters(filters);

        result.StockStatus.Should().Be(ProductStockStatus.OutOfStock);
        result.Stock.Should().Be(new ProductsNumberFilter(ProductsNumberOperator.LessThanOrEqual, 0m));
    }

    // ── Numeric (Price/Stock) translation ───────────────────────────

    [Theory]
    [InlineData(FilterOperator.Equals, ProductsNumberOperator.Equals)]
    [InlineData(FilterOperator.NotEquals, ProductsNumberOperator.NotEquals)]
    [InlineData(FilterOperator.GreaterThan, ProductsNumberOperator.GreaterThan)]
    [InlineData(FilterOperator.GreaterThanOrEquals, ProductsNumberOperator.GreaterThanOrEqual)]
    [InlineData(FilterOperator.LessThan, ProductsNumberOperator.LessThan)]
    [InlineData(FilterOperator.LessThanOrEquals, ProductsNumberOperator.LessThanOrEqual)]
    public void TranslateFilters_NumberOperators_MapOneToOne(
        FilterOperator radzenOperator, ProductsNumberOperator expected)
    {
        var filters = new[]
        {
            new FilterDescriptor
            {
                Property = "Price.Amount", FilterValue = 99m, FilterOperator = radzenOperator
            }
        };

        var result = ProductsGridStateTranslator.TranslateFilters(filters);

        result.Price.Should().Be(new ProductsNumberFilter(expected, 99m));
    }

    [Fact]
    public void TranslateFilters_NonNumberOperatorOnNumberColumn_Skipped()
    {
        // A text operator on a numeric column is untranslatable —
        // skipped (lenient).
        var filters = new[]
        {
            new FilterDescriptor
            {
                Property = "Price.Amount", FilterValue = 99m, FilterOperator = FilterOperator.Contains
            }
        };

        var result = ProductsGridStateTranslator.TranslateFilters(filters);

        result.Price.Should().BeNull();
    }

    [Theory]
    [InlineData(99)]        // int (the Stock column's CLR type)
    [InlineData(99L)]       // long
    [InlineData(99.5)]      // double
    [InlineData("99.5")]    // string form (a deserialized filter state)
    public void TranslateFilters_NumberValueForms_AllConvert(object value)
    {
        var filters = new[]
        {
            new FilterDescriptor
            {
                Property = "Price.Amount", FilterValue = value, FilterOperator = FilterOperator.Equals
            }
        };

        var result = ProductsGridStateTranslator.TranslateFilters(filters);

        // All numeric CLR forms (and the invariant string form) land on
        // the SAME typed decimal filter.
        var expected = Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
        result.Price.Should().Be(
            new ProductsNumberFilter(ProductsNumberOperator.Equals, expected),
            $"the {value.GetType().Name} form converts to the typed decimal filter");
    }

    [Fact]
    public void TranslateFilters_NonNumericStringValue_Skipped()
    {
        var filters = new[]
        {
            new FilterDescriptor
            {
                Property = "Price.Amount", FilterValue = "cheap", FilterOperator = FilterOperator.Equals
            }
        };

        var result = ProductsGridStateTranslator.TranslateFilters(filters);

        result.Price.Should().BeNull();
    }

    // ── Category columns ────────────────────────────────────────────

    [Fact]
    public void TranslateFilters_CategoryColumns_PassThroughAsTextFilters()
    {
        // The category columns' text filters pass through untouched — the
        // QUERY HANDLER resolves them against the category tree into Id
        // sets (the product row stores only Guid FKs).
        var filters = new[]
        {
            new FilterDescriptor
            {
                Property = "CategoryName", FilterValue = "Elec", FilterOperator = FilterOperator.Contains
            },
            new FilterDescriptor
            {
                Property = "SubCategoryName", FilterValue = "laptop", FilterOperator = FilterOperator.DoesNotContain
            },
            new FilterDescriptor
            {
                Property = "SubSubCategoryName", FilterValue = "gaming", FilterOperator = FilterOperator.Equals
            }
        };

        var result = ProductsGridStateTranslator.TranslateFilters(filters);

        result.CategoryName.Should().Be(new ProductsTextFilter("Elec", ProductsTextOperator.Contains));
        result.SubCategoryName.Should().Be(new ProductsTextFilter("laptop", ProductsTextOperator.NotContains));
        result.SubSubCategoryName.Should().Be(new ProductsTextFilter("gaming", ProductsTextOperator.Equals));
    }

    [Fact]
    public void TranslateFilters_UnknownProperty_Skipped()
    {
        var filters = new[]
        {
            new FilterDescriptor
            {
                Property = "SomeNewColumn", FilterValue = "x", FilterOperator = FilterOperator.Contains
            }
        };

        var result = ProductsGridStateTranslator.TranslateFilters(filters);

        result.Name.Should().BeNull();
        result.CategoryName.Should().BeNull();
        result.StockStatus.Should().BeNull();
    }
}
