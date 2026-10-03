using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace TakOne.WebUI.Services;

/// <summary>
/// Query-string persistence helpers for the grid pages (AdminUsers,
/// AdminProducts, Sales) and the shop Products page.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THE URL (not localStorage)</b>: a refresh (F5 / circuit drop +
/// reconnect) re-requests the page's URL — state baked into that URL
/// survives with zero extra round-trips, works during prerender, is
/// visible/shareable/bookmarkable, and needs no JS interop. localStorage
/// would require an OnAfterRender read + a second load before the grid
/// even shows the right page.
/// </para>
/// <para>
/// <b>ONLY NON-DEFAULT STATE IS WRITTEN</b>: the helpers omit null/empty
/// parameters, and the pages only include a parameter when it differs
/// from its default (page 1, page size 20, no filter). A freshly opened
/// page keeps its clean URL (<c>/Admin/Users</c>), and typing in a search
/// box grows it (<c>/Admin/Users?wid=123</c>). The no-defaults rule also
/// keeps bUnit page tests untouched: their loads run with default state,
/// so no URL replacement ever fires.
/// </para>
/// <para>
/// <b>REPLACE, NOT PUSH</b>: every write uses
/// <c>NavigateTo(uri, forceLoad: false, replace: true)</c> — the address
/// bar updates without a new history entry (search-as-you-type must not
/// flood the Back stack) and without a full reload (the Blazor circuit
/// survives). The pages don't override <c>OnParametersSet</c>, so the
/// same-route query change re-renders nothing by itself.
/// </para>
/// <para>
/// <b>URI COMPARISON</b>: writes are skipped when the target URI equals
/// the current one — the restore-triggered load must not rewrite the URL
/// it just read (and steady-state reloads — e.g. the heal's
/// <c>FirstPage(true)</c> — don't touch the address bar either).
/// </para>
/// </remarks>
public static class GridUrlState
{
    /// <summary>
    /// Parses the CURRENT URL's query string into a plain string
    /// dictionary (first value wins for repeated keys). Returns an empty
    /// dictionary when the URL has no query.
    /// </summary>
    public static Dictionary<string, string> CurrentQuery(NavigationManager navigation)
    {
        var query = navigation.ToAbsoluteUri(navigation.Uri).Query;
        if (string.IsNullOrEmpty(query))
        {
            return new Dictionary<string, string>();
        }

        var parsed = QueryHelpers.ParseQuery(query);
        var result = new Dictionary<string, string>(parsed.Count, StringComparer.Ordinal);
        foreach (var pair in parsed)
        {
            result[pair.Key] = pair.Value.ToString();
        }

        return result;
    }

    /// <summary>
    /// Builds the page's relative URI (existing path + the given
    /// parameters) from key/value pairs. Null/empty values are OMITTED
    /// (see the class remarks' only-non-default rule). Parameter order is
    /// preserved — the caller passes a stable order so repeated writes
    /// produce byte-identical URIs (the unchanged-URI skip depends on it).
    /// </summary>
    public static string BuildRelative(NavigationManager navigation, IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        // The current path WITHOUT the query — ToBaseRelativePath strips
        // the base and we cut everything from '?' on.
        var relative = navigation.ToBaseRelativePath(navigation.Uri);
        var path = relative.Split('?', 2)[0];

        var present = parameters
            .Where(p => !string.IsNullOrEmpty(p.Value))
            .Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))
            .ToList();

        if (present.Count == 0)
        {
            return path;
        }

        // AddQueryString handles URL-encoding; a leading '?' is appended
        // automatically.
        return QueryHelpers.AddQueryString(path, present);
    }

    /// <summary>
    /// Replaces the browser URL with the given relative URI — unless it
    /// already IS the current URI (see the class remarks). Never force-
    /// loads; never adds a history entry.
    /// </summary>
    public static void ReplaceIfChanged(NavigationManager navigation, string relativeUri)
    {
        var absoluteTarget = navigation.ToAbsoluteUri(relativeUri).PathAndQuery;
        var current = navigation.ToAbsoluteUri(navigation.Uri).PathAndQuery;

        if (string.Equals(absoluteTarget, current, StringComparison.Ordinal))
        {
            return; // already there — rewriting would be a no-op churn
        }

        navigation.NavigateTo(relativeUri, forceLoad: false, replace: true);
    }
}
