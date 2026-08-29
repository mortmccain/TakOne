using TakOne.Application.Users.Queries.GetUsersPaginated;
using TakOne.Domain.Users;

namespace TakOne.WebUI.Services;

/// <summary>
/// Translates the Radzen grid's LoadDataArgs descriptors into the typed
/// users-list query fields (Round 5 — server-driven paging for the
/// AdminUsers grid). Mirrors <see cref="SalesGridStateTranslator"/> from
/// Round 4.
/// CONTRACT (pinned against Radzen 11.1.8 by DataGridLoadDataContractTests):
/// the grid re-raises LoadData with the COMPLETE active filter set on every
/// load — a cleared filter disappears from args.Filters rather than arriving
/// as a null-valued descriptor. Sorts arrive as structured SortDescriptors;
/// filters as FilterDescriptors (property + raw value + operator).
/// LENIENT BY DESIGN: unknown properties, unparseable values, and
/// untranslatable operators are skipped, never thrown.
/// </summary>
public static class UsersGridStateTranslator
{
    /// <summary>
    /// The per-column filter bundle extracted from one LoadData event —
    /// mirrors <see cref="SalesGridStateTranslator.ColumnFilters"/>.
    /// </summary>
    public sealed record ColumnFilters(
        UsersTextFilter? WorkerId,
        UsersTextFilter? FullName,
        Gender? Gender,
        Guid? GroupId,
        bool? IsActive);

    /// <summary>
    /// Translates the grid's sort descriptors into the typed sort key +
    /// direction. Nulls (no user sort active) map to the repository's
    /// FullName-ascending default — NOT a descending newest-first default
    /// like the sales list: the pre-Round-5 users grid was FullName-ordered
    /// and the mobile list + typeahead still rely on that order.
    /// </summary>
    public static (UsersSortBy? SortBy, bool SortDescending) TranslateSort(
        IEnumerable<Radzen.SortDescriptor>? sorts)
    {
        var sort = sorts?.FirstOrDefault();
        if (sort is null || string.IsNullOrEmpty(sort.Property))
            return (null, false); // no user sort → repo default (FullName asc)

        var sortBy = sort.Property switch
        {
            "WorkerId" => UsersSortBy.WorkerId,
            "FullName" => UsersSortBy.FullName,
            "Gender" => UsersSortBy.Gender,
            "IsActive" => UsersSortBy.IsActive,
            // "GroupName" is deliberately absent: the group name lives on
            // the CustomerGroup aggregate, so a name sort would need a
            // cross-table join — the column's header sort is disabled in
            // the UI and an unexpected descriptor is skipped here.
            _ => (UsersSortBy?)null
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
        var result = new ColumnFilters(null, null, null, null, null);
        foreach (var descriptor in filters ?? Enumerable.Empty<Radzen.FilterDescriptor>())
        {
            if (descriptor.FilterValue is null) continue; // AllowClear'd descriptor

            switch (descriptor.Property)
            {
                case "WorkerId":
                    result = result with { WorkerId = TranslateTextFilter(descriptor) };
                    break;

                case "FullName":
                    result = result with { FullName = TranslateTextFilter(descriptor) };
                    break;

                case "Gender":
                    result = result with { Gender = TranslateGender(descriptor.FilterValue) };
                    break;

                case "GroupName":
                    // The Group column's FilterTemplate dropdown binds to the
                    // group's Guid (stable across renames), so the filter
                    // travels server-side as a GroupId exact-match.
                    result = result with { GroupId = TranslateGroupId(descriptor) };
                    break;

                case "IsActive":
                    result = result with { IsActive = TranslateIsActive(descriptor.FilterValue) };
                    break;

                // Unknown property: skipped (lenient).
            }
        }
        return result;
    }

    private static UsersTextFilter? TranslateTextFilter(Radzen.FilterDescriptor descriptor)
    {
        if (descriptor.FilterValue is null || !IsTextishOperator(descriptor.FilterOperator))
            return null;
        var value = descriptor.FilterValue.ToString();
        if (string.IsNullOrWhiteSpace(value)) return null;
        var op = descriptor.FilterOperator switch
        {
            Radzen.FilterOperator.Contains => UsersTextOperator.Contains,
            Radzen.FilterOperator.DoesNotContain => UsersTextOperator.NotContains,
            Radzen.FilterOperator.Equals => UsersTextOperator.Equals,
            Radzen.FilterOperator.NotEquals => UsersTextOperator.NotEquals,
            Radzen.FilterOperator.StartsWith => UsersTextOperator.StartsWith,
            Radzen.FilterOperator.EndsWith => UsersTextOperator.EndsWith,
            _ => (UsersTextOperator?)null
        };
        return op.HasValue ? new UsersTextFilter(value, op.Value) : null;
    }

    private static bool IsTextishOperator(Radzen.FilterOperator op) =>
        op is Radzen.FilterOperator.Contains or Radzen.FilterOperator.DoesNotContain or
           Radzen.FilterOperator.Equals or Radzen.FilterOperator.NotEquals or
           Radzen.FilterOperator.StartsWith or Radzen.FilterOperator.EndsWith;

    /// <summary>
    /// The Gender column's FilterTemplate dropdown sets the enum value
    /// directly; the built-in filter menu could also deliver the enum as a
    /// string ("Male"/"Female"/"0"/"1"). All forms are accepted.
    /// </summary>
    private static Gender? TranslateGender(object? value)
    {
        if (value is Gender gender) return gender;
        if (value is null) return null;
        return value.ToString() switch
        {
            "Male" => Gender.Male,
            "Female" => Gender.Female,
            _ => Enum.TryParse<Gender>(value.ToString(), ignoreCase: true, out var parsed)
                ? parsed
                : null
        };
    }

    /// <summary>
    /// The Status column's FilterTemplate dropdown maps its "Active"/
    /// "Inactive" keys to a bool before writing column.FilterValue; the
    /// string forms are accepted too (defense against the filter menu's
    /// free-text path).
    /// </summary>
    private static bool? TranslateIsActive(object? value)
    {
        if (value is bool isActive) return isActive;
        if (value is null) return null;
        return value.ToString() switch
        {
            "Active" => true,
            "Inactive" => false,
            "true" => true,
            "false" => false,
            _ => null
        };
    }

    private static Guid? TranslateGroupId(Radzen.FilterDescriptor descriptor)
    {
        if (descriptor.FilterValue is Guid groupId) return groupId;
        if (descriptor.FilterOperator != Radzen.FilterOperator.Equals) return null;
        return Guid.TryParse(descriptor.FilterValue.ToString(), out var parsed)
            ? parsed
            : null;
    }
}
