using FluentAssertions;
using Radzen;
using TakOne.Application.Sales.Queries.GetSalesPaginated;
using TakOne.Application.Sales.Specifications;
using TakOne.Domain.Sales.Enums;
using TakOne.WebUI.Services;
using Xunit;

namespace TakOne.WebUI.Tests.Services;

/// <summary>
/// Unit tests for <see cref="SalesGridStateTranslator"/> — the Radzen
/// LoadData descriptor → typed query translation used by the Sales
/// grid (Round 4 — server-driven paging).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THIS LAYER</b>: the translation is the only non-trivial logic
/// in the Sales page's LoadData path. Keeping it in a pure static class
/// (instead of in the page's private methods) lets these tests pin
/// every mapping decision — column property names, operator enums,
/// value conversions, and the lenient-skip behavior for unknown shapes
/// — without a full-page bUnit context (IMessageBus, auth state,
/// localizer, …).
/// </para>
/// <para>
/// The Radzen side of the contract (that the grid actually DELIVERS
/// these descriptor shapes in LoadData mode) is pinned separately by
/// <c>DataGridLoadDataContractTests</c> in the ComponentTests project.
/// </para>
/// </remarks>
public class SalesGridStateTranslatorTests
{
    // ── Sort translation ─────────────────────────────────────────────

    [Fact]
    public void TranslateSort_NoSorts_DefaultsToNewestFirst()
    {
        var (sortBy, descending) = SalesGridStateTranslator.TranslateSort(null);

        sortBy.Should().BeNull("no user sort active");
        descending.Should().BeTrue("the spec default is newest-first");
    }

    [Fact]
    public void TranslateSort_EmptySorts_DefaultsToNewestFirst()
    {
        var (sortBy, descending) = SalesGridStateTranslator.TranslateSort(
            Array.Empty<SortDescriptor>());

        sortBy.Should().BeNull();
        descending.Should().BeTrue();
    }

    [Theory]
    [InlineData("DisplayNumber", SalesSortBy.SaleNumber)]
    [InlineData("Status", SalesSortBy.Status)]
    [InlineData("Total.Amount", SalesSortBy.Total)]
    [InlineData("CustomerName", SalesSortBy.CustomerName)]
    [InlineData("CreatedByName", SalesSortBy.CreatedByName)]
    [InlineData("CreatedAtUtc", SalesSortBy.CreatedAtUtc)]
    public void TranslateSort_KnownColumns_MapToSortKeys(
        string property, SalesSortBy expected)
    {
        var (sortBy, _) = SalesGridStateTranslator.TranslateSort(
            new[] { new SortDescriptor { Property = property, SortOrder = SortOrder.Ascending } });

        sortBy.Should().Be(expected);
    }

    [Fact]
    public void TranslateSort_DescendingOrder_SetsDescendingFlag()
    {
        var (sortBy, descending) = SalesGridStateTranslator.TranslateSort(
            new[] { new SortDescriptor { Property = "Total.Amount", SortOrder = SortOrder.Descending } });

        sortBy.Should().Be(SalesSortBy.Total);
        descending.Should().BeTrue();
    }

    [Fact]
    public void TranslateSort_UnknownProperty_YieldsNoSortKey()
    {
        // A future Radzen version (or a renamed column) must degrade to
        // the default sort, not throw.
        var (sortBy, descending) = SalesGridStateTranslator.TranslateSort(
            new[] { new SortDescriptor { Property = "SomeNewColumn", SortOrder = SortOrder.Ascending } });

        sortBy.Should().BeNull("unknown properties fall back to the default sort");
        descending.Should().BeTrue();
    }

    [Fact]
    public void TranslateSort_SecondaryDescriptors_AreIgnored()
    {
        // SortMode is single-key; only the first descriptor is honored
        // (the spec's Id tiebreaker keeps the result deterministic).
        var sorts = new[]
        {
            new SortDescriptor { Property = "Status", SortOrder = SortOrder.Ascending },
            new SortDescriptor { Property = "CustomerName", SortOrder = SortOrder.Descending }
        };

        var (sortBy, descending) = SalesGridStateTranslator.TranslateSort(sorts);

        sortBy.Should().Be(SalesSortBy.Status);
        descending.Should().BeFalse();
    }

    // ── Filter translation ───────────────────────────────────────────

    [Fact]
    public void TranslateFilters_NullFilters_EverythingClear()
    {
        var result = SalesGridStateTranslator.TranslateFilters(null);

        result.SaleNumberTerm.Should().BeNull();
        result.CustomerName.Should().BeNull();
        result.CreatedByName.Should().BeNull();
        result.Total.Should().BeNull();
        result.Status.Should().BeNull();
    }

    [Fact]
    public void TranslateFilters_NullValuedDescriptor_IsSkipped()
    {
        // AllowClear'd filters may still emit a null-valued descriptor.
        var result = SalesGridStateTranslator.TranslateFilters(new[]
        {
            new FilterDescriptor { Property = "CustomerName", FilterValue = null }
        });

        result.CustomerName.Should().BeNull();
    }

    [Fact]
    public void TranslateFilters_CompleteSet_MapsEveryColumn()
    {
        var result = SalesGridStateTranslator.TranslateFilters(new[]
        {
            new FilterDescriptor
            {
                Property = "DisplayNumber",
                FilterValue = "INT-1405-42",
                FilterOperator = FilterOperator.Contains
            },
            new FilterDescriptor
            {
                Property = "Status",
                FilterValue = "Pending",
                FilterOperator = FilterOperator.Equals
            },
            new FilterDescriptor
            {
                Property = "Total.Amount",
                FilterValue = 150m,
                FilterOperator = FilterOperator.GreaterThanOrEquals
            },
            new FilterDescriptor
            {
                Property = "CustomerName",
                FilterValue = "Bob",
                FilterOperator = FilterOperator.StartsWith
            },
            new FilterDescriptor
            {
                Property = "CreatedByName",
                FilterValue = "Staff",
                FilterOperator = FilterOperator.DoesNotContain
            }
        });

        result.SaleNumberTerm.Should().Be("INT-1405-42");
        result.Status.Should().Be(SaleStatus.Pending);
        result.Total.Should().Be(
            new SalesAmountFilter(SalesAmountOperator.GreaterThanOrEqual, 150m));
        result.CustomerName.Should().Be(
            new SalesTextFilter("Bob", SalesTextOperator.StartsWith));
        result.CreatedByName.Should().Be(
            new SalesTextFilter("Staff", SalesTextOperator.NotContains));
    }

    [Fact]
    public void TranslateFilters_ClearedFilterDisappearsFromSet()
    {
        // The contract: the grid re-delivers the COMPLETE active set —
        // a cleared filter is simply absent on the next load. Two
        // consecutive translations must therefore reflect removals.
        var withFilter = new[]
        {
            new FilterDescriptor
            {
                Property = "CustomerName",
                FilterValue = "Bob",
                FilterOperator = FilterOperator.Contains
            }
        };
        var withoutFilter = Array.Empty<FilterDescriptor>();

        var first = SalesGridStateTranslator.TranslateFilters(withFilter);
        var second = SalesGridStateTranslator.TranslateFilters(withoutFilter);

        first.CustomerName.Should().NotBeNull();
        second.CustomerName.Should().BeNull(
            "an absent descriptor means the filter was cleared");
    }

    [Theory]
    [InlineData(FilterOperator.Contains, SalesTextOperator.Contains)]
    [InlineData(FilterOperator.DoesNotContain, SalesTextOperator.NotContains)]
    [InlineData(FilterOperator.Equals, SalesTextOperator.Equals)]
    [InlineData(FilterOperator.NotEquals, SalesTextOperator.NotEquals)]
    [InlineData(FilterOperator.StartsWith, SalesTextOperator.StartsWith)]
    [InlineData(FilterOperator.EndsWith, SalesTextOperator.EndsWith)]
    public void TranslateFilters_TextOperators_MapOneToOne(
        FilterOperator radzen, SalesTextOperator expected)
    {
        var result = SalesGridStateTranslator.TranslateFilters(new[]
        {
            new FilterDescriptor
            {
                Property = "CustomerName",
                FilterValue = "x",
                FilterOperator = radzen
            }
        });

        result.CustomerName.Should().Be(new SalesTextFilter("x", expected));
    }

    [Theory]
    [InlineData(FilterOperator.Equals, SalesAmountOperator.Equals)]
    [InlineData(FilterOperator.NotEquals, SalesAmountOperator.NotEquals)]
    [InlineData(FilterOperator.GreaterThan, SalesAmountOperator.GreaterThan)]
    [InlineData(FilterOperator.GreaterThanOrEquals, SalesAmountOperator.GreaterThanOrEqual)]
    [InlineData(FilterOperator.LessThan, SalesAmountOperator.LessThan)]
    [InlineData(FilterOperator.LessThanOrEquals, SalesAmountOperator.LessThanOrEqual)]
    public void TranslateFilters_AmountOperators_MapOneToOne(
        FilterOperator radzen, SalesAmountOperator expected)
    {
        var result = SalesGridStateTranslator.TranslateFilters(new[]
        {
            new FilterDescriptor
            {
                Property = "Total.Amount",
                FilterValue = 10m,
                FilterOperator = radzen
            }
        });

        result.Total.Should().Be(new SalesAmountFilter(expected, 10m));
    }

    [Fact]
    public void TranslateFilters_NumericValueTypes_AllConvert()
    {
        // Radzen parses the input into the column property's CLR type —
        // int, long, and double have all been observed across versions.
        var result = SalesGridStateTranslator.TranslateFilters(new[]
        {
            new FilterDescriptor
            {
                Property = "Total.Amount", FilterValue = 100, FilterOperator = FilterOperator.Equals
            },
            new FilterDescriptor
            {
                Property = "CustomerName", FilterValue = "never matches", FilterOperator = FilterOperator.Equals
            }
        });

        // Only the LAST Total.Amount descriptor survives? No — the
        // translator folds them via `with`, so the last one wins; here
        // there is only one Total descriptor, valued 100 (int).
        result.Total.Should().Be(new SalesAmountFilter(SalesAmountOperator.Equals, 100m));
    }

    [Fact]
    public void TranslateFilters_UntranslatableValueOnAmount_IsSkipped()
    {
        var result = SalesGridStateTranslator.TranslateFilters(new[]
        {
            new FilterDescriptor
            {
                Property = "Total.Amount",
                FilterValue = "not a number",
                FilterOperator = FilterOperator.Equals
            }
        });

        result.Total.Should().BeNull("an unconvertible value must be skipped, not thrown");
    }

    [Fact]
    public void TranslateFilters_StatusCasing_IsCaseInsensitive()
    {
        var result = SalesGridStateTranslator.TranslateFilters(new[]
        {
            new FilterDescriptor
            {
                Property = "Status", FilterValue = "invoiced", FilterOperator = FilterOperator.Equals
            }
        });

        result.Status.Should().Be(SaleStatus.Invoiced);
    }

    [Fact]
    public void TranslateFilters_UnknownStatus_IsSkipped()
    {
        var result = SalesGridStateTranslator.TranslateFilters(new[]
        {
            new FilterDescriptor
            {
                Property = "Status", FilterValue = "SomeFutureStatus", FilterOperator = FilterOperator.Equals
            }
        });

        result.Status.Should().BeNull("an unparseable status must be skipped, not thrown");
    }

    [Fact]
    public void TranslateFilters_NonTextOperatorOnNumberColumn_IsSkipped()
    {
        // The number column only carries a meaningful term for text-ish
        // operators; IsNull/In/etc. carry nothing translatable.
        var result = SalesGridStateTranslator.TranslateFilters(new[]
        {
            new FilterDescriptor
            {
                Property = "DisplayNumber", FilterValue = "anything", FilterOperator = FilterOperator.IsNull
            }
        });

        result.SaleNumberTerm.Should().BeNull();
    }

    [Fact]
    public void TranslateFilters_UnknownProperty_IsSkipped()
    {
        var result = SalesGridStateTranslator.TranslateFilters(new[]
        {
            new FilterDescriptor
            {
                Property = "SomeNewColumn", FilterValue = "x", FilterOperator = FilterOperator.Contains
            }
        });

        result.SaleNumberTerm.Should().BeNull();
        result.CustomerName.Should().BeNull();
        result.CreatedByName.Should().BeNull();
        result.Total.Should().BeNull();
        result.Status.Should().BeNull();
    }
}
