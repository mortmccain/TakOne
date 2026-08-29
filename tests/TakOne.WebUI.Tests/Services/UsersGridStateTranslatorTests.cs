using FluentAssertions;
using Radzen;
using TakOne.Application.Users.Queries.GetUsersPaginated;
using TakOne.Domain.Users;
using TakOne.WebUI.Services;
using Xunit;

namespace TakOne.WebUI.Tests.Services;

/// <summary>
/// Unit tests for <see cref="UsersGridStateTranslator"/> — the Radzen
/// LoadData descriptor → typed query translation used by the AdminUsers
/// grid (Round 5 — server-driven paging). Mirrors
/// <see cref="SalesGridStateTranslatorTests"/> from Round 4.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THIS LAYER</b>: the translation is the only non-trivial logic
/// in the AdminUsers page's LoadData path. Keeping it in a pure static
/// class lets these tests pin every mapping decision — column property
/// names, operator enums, value conversions (Guid group ids, Gender
/// enum/string forms, IsActive bool/string forms), and the lenient-skip
/// behavior for unknown shapes — without a full-page bUnit context.
/// </para>
/// <para>
/// The Radzen side of the contract (that the grid actually DELIVERS these
/// descriptor shapes in LoadData mode) is pinned separately by
/// <c>DataGridLoadDataContractTests</c> in the ComponentTests project.
/// </para>
/// </remarks>
public class UsersGridStateTranslatorTests
{
    // ── Sort translation ─────────────────────────────────────────────

    [Fact]
    public void TranslateSort_NoSorts_DefaultsToFullNameAscending()
    {
        var (sortBy, descending) = UsersGridStateTranslator.TranslateSort(null);

        sortBy.Should().BeNull("no user sort active");
        descending.Should().BeFalse(
            "the users-list default is FullName ASCENDING (the pre-Round-5 order " +
            "the mobile list + typeahead rely on) — NOT the sales list's newest-first");
    }

    [Fact]
    public void TranslateSort_EmptySorts_DefaultsToFullNameAscending()
    {
        var (sortBy, descending) = UsersGridStateTranslator.TranslateSort(
            Array.Empty<SortDescriptor>());

        sortBy.Should().BeNull();
        descending.Should().BeFalse();
    }

    [Theory]
    [InlineData("WorkerId", UsersSortBy.WorkerId)]
    [InlineData("FullName", UsersSortBy.FullName)]
    [InlineData("Gender", UsersSortBy.Gender)]
    [InlineData("IsActive", UsersSortBy.IsActive)]
    [InlineData("GroupName", UsersSortBy.GroupName)]
    public void TranslateSort_KnownColumns_MapToSortKeys(
        string property, UsersSortBy expected)
    {
        var (sortBy, _) = UsersGridStateTranslator.TranslateSort(
            new[] { new SortDescriptor { Property = property, SortOrder = SortOrder.Ascending } });

        sortBy.Should().Be(expected);
    }

    [Fact]
    public void TranslateSort_UnknownColumn_YieldsNoSortKey()
    {
        // A stale/deserialized descriptor (e.g. a preserved grid state
        // for a column that no longer exists) must degrade to the default
        // sort, not throw.
        var (sortBy, _) = UsersGridStateTranslator.TranslateSort(
            new[] { new SortDescriptor { Property = "LegacyColumn", SortOrder = SortOrder.Ascending } });

        sortBy.Should().BeNull("unknown properties are skipped leniently");
    }

    [Fact]
    public void TranslateSort_DescendingOrder_SetsDescendingFlag()
    {
        var (sortBy, descending) = UsersGridStateTranslator.TranslateSort(
            new[] { new SortDescriptor { Property = "FullName", SortOrder = SortOrder.Descending } });

        sortBy.Should().Be(UsersSortBy.FullName);
        descending.Should().BeTrue();
    }

    [Fact]
    public void TranslateSort_UnknownProperty_YieldsNoSortKey()
    {
        var (sortBy, _) = UsersGridStateTranslator.TranslateSort(
            new[] { new SortDescriptor { Property = "SomeNewColumn", SortOrder = SortOrder.Ascending } });

        sortBy.Should().BeNull("unknown properties fall back to the default sort");
    }

    [Fact]
    public void TranslateSort_SecondaryDescriptors_AreIgnored()
    {
        var sorts = new[]
        {
            new SortDescriptor { Property = "WorkerId", SortOrder = SortOrder.Ascending },
            new SortDescriptor { Property = "FullName", SortOrder = SortOrder.Descending }
        };

        var (sortBy, descending) = UsersGridStateTranslator.TranslateSort(sorts);

        sortBy.Should().Be(UsersSortBy.WorkerId);
        descending.Should().BeFalse();
    }

    // ── Filter translation ───────────────────────────────────────────

    [Fact]
    public void TranslateFilters_NullFilters_EverythingClear()
    {
        var result = UsersGridStateTranslator.TranslateFilters(null);

        result.WorkerId.Should().BeNull();
        result.FullName.Should().BeNull();
        result.Gender.Should().BeNull();
        result.GroupId.Should().BeNull();
        result.IsActive.Should().BeNull();
    }

    [Fact]
    public void TranslateFilters_NullValuedDescriptor_Skipped()
    {
        // An AllowClear'd dropdown clears its descriptor's value — the
        // filter simply disappears from the set (pinned Radzen contract).
        var filters = new[]
        {
            new FilterDescriptor { Property = "IsActive", FilterValue = null }
        };

        var result = UsersGridStateTranslator.TranslateFilters(filters);

        result.IsActive.Should().BeNull();
    }

    [Fact]
    public void TranslateFilters_CompleteSet_MapsEveryColumn()
    {
        var groupId = Guid.NewGuid();
        var filters = new[]
        {
            new FilterDescriptor
            {
                Property = "WorkerId", FilterValue = "EMP-1", FilterOperator = FilterOperator.Contains
            },
            new FilterDescriptor
            {
                Property = "FullName", FilterValue = "smith", FilterOperator = FilterOperator.StartsWith
            },
            new FilterDescriptor
            {
                Property = "Gender", FilterValue = Gender.Female, FilterOperator = FilterOperator.Equals
            },
            new FilterDescriptor
            {
                Property = "GroupName", FilterValue = groupId, FilterOperator = FilterOperator.Equals
            },
            new FilterDescriptor
            {
                Property = "IsActive", FilterValue = true, FilterOperator = FilterOperator.Equals
            }
        };

        var result = UsersGridStateTranslator.TranslateFilters(filters);

        result.WorkerId.Should().Be(new UsersTextFilter("EMP-1", UsersTextOperator.Contains));
        result.FullName.Should().Be(new UsersTextFilter("smith", UsersTextOperator.StartsWith));
        result.Gender.Should().Be(Gender.Female);
        result.GroupId.Should().Be(groupId);
        result.IsActive.Should().BeTrue();
    }

    [Theory]
    [InlineData(FilterOperator.Contains, UsersTextOperator.Contains)]
    [InlineData(FilterOperator.DoesNotContain, UsersTextOperator.NotContains)]
    [InlineData(FilterOperator.Equals, UsersTextOperator.Equals)]
    [InlineData(FilterOperator.NotEquals, UsersTextOperator.NotEquals)]
    [InlineData(FilterOperator.StartsWith, UsersTextOperator.StartsWith)]
    [InlineData(FilterOperator.EndsWith, UsersTextOperator.EndsWith)]
    public void TranslateFilters_TextOperators_MapOneToOne(
        FilterOperator radzenOperator, UsersTextOperator expected)
    {
        var filters = new[]
        {
            new FilterDescriptor
            {
                Property = "WorkerId", FilterValue = "x", FilterOperator = radzenOperator
            }
        };

        var result = UsersGridStateTranslator.TranslateFilters(filters);

        result.WorkerId.Should().Be(new UsersTextFilter("x", expected));
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
                Property = "WorkerId", FilterValue = "x", FilterOperator = FilterOperator.GreaterThan
            }
        };

        var result = UsersGridStateTranslator.TranslateFilters(filters);

        result.WorkerId.Should().BeNull();
    }

    [Fact]
    public void TranslateFilters_WhitespaceTextValue_Skipped()
    {
        var filters = new[]
        {
            new FilterDescriptor
            {
                Property = "FullName", FilterValue = "   ", FilterOperator = FilterOperator.Contains
            }
        };

        var result = UsersGridStateTranslator.TranslateFilters(filters);

        result.FullName.Should().BeNull();
    }

    [Fact]
    public void TranslateFilters_GenderAsStringKey_MapsToEnum()
    {
        // The dropdown writes the enum; the string key form is accepted
        // as defense (e.g. a deserialized filter state).
        var filters = new[]
        {
            new FilterDescriptor
            {
                Property = "Gender", FilterValue = "Female", FilterOperator = FilterOperator.Equals
            }
        };

        var result = UsersGridStateTranslator.TranslateFilters(filters);

        result.Gender.Should().Be(Gender.Female);
    }

    [Fact]
    public void TranslateFilters_GenderAsUnparseableString_Skipped()
    {
        var filters = new[]
        {
            new FilterDescriptor
            {
                Property = "Gender", FilterValue = "Robot", FilterOperator = FilterOperator.Equals
            }
        };

        var result = UsersGridStateTranslator.TranslateFilters(filters);

        result.Gender.Should().BeNull();
    }

    [Fact]
    public void TranslateFilters_IsActiveAsStringKey_MapsToBool()
    {
        var activeFilters = new[]
        {
            new FilterDescriptor
            {
                Property = "IsActive", FilterValue = "Active", FilterOperator = FilterOperator.Equals
            }
        };
        var inactiveFilters = new[]
        {
            new FilterDescriptor
            {
                Property = "IsActive", FilterValue = "Inactive", FilterOperator = FilterOperator.Equals
            }
        };

        UsersGridStateTranslator.TranslateFilters(activeFilters).IsActive.Should().BeTrue();
        UsersGridStateTranslator.TranslateFilters(inactiveFilters).IsActive.Should().BeFalse();
    }

    [Fact]
    public void TranslateFilters_IsActiveAsUnparseableString_Skipped()
    {
        var filters = new[]
        {
            new FilterDescriptor
            {
                Property = "IsActive", FilterValue = "Maybe", FilterOperator = FilterOperator.Equals
            }
        };

        var result = UsersGridStateTranslator.TranslateFilters(filters);

        result.IsActive.Should().BeNull();
    }

    [Fact]
    public void TranslateFilters_GroupNameAsGuidString_MapsToGroupId()
    {
        // The dropdown binds the group's Guid; a string-form Guid (e.g.
        // from a deserialized filter state) parses to the same value.
        var groupId = Guid.NewGuid();
        var filters = new[]
        {
            new FilterDescriptor
            {
                Property = "GroupName", FilterValue = groupId.ToString(), FilterOperator = FilterOperator.Equals
            }
        };

        var result = UsersGridStateTranslator.TranslateFilters(filters);

        result.GroupId.Should().Be(groupId);
    }

    [Fact]
    public void TranslateFilters_GroupNameAsNonGuidString_Skipped()
    {
        // A legacy filter value (the pre-Round-5 dropdown bound group
        // NAME strings) is untranslatable as an Id — skipped rather than
        // guessed (a name→id lookup would race renames).
        var filters = new[]
        {
            new FilterDescriptor
            {
                Property = "GroupName", FilterValue = "VIP Customers", FilterOperator = FilterOperator.Equals
            }
        };

        var result = UsersGridStateTranslator.TranslateFilters(filters);

        result.GroupId.Should().BeNull();
    }

    [Fact]
    public void TranslateFilters_GroupNameWithNonEqualsOperator_Skipped()
    {
        var filters = new[]
        {
            new FilterDescriptor
            {
                Property = "GroupName", FilterValue = Guid.NewGuid().ToString(),
                FilterOperator = FilterOperator.Contains
            }
        };

        var result = UsersGridStateTranslator.TranslateFilters(filters);

        result.GroupId.Should().BeNull("only exact group matches are meaningful");
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

        var result = UsersGridStateTranslator.TranslateFilters(filters);

        result.WorkerId.Should().BeNull();
        result.FullName.Should().BeNull();
        result.GroupId.Should().BeNull();
    }
}
