namespace TakOne.Application.Common.Authorization;

/// <summary>
/// Named authorization policies registered in <c>Program.cs</c> via
/// <c>builder.Services.AddAuthorization(options =&gt; options.AddPolicy(...))</c>.
/// Blazor pages reference these policies via
/// <c>[Authorize(Policy = "PolicyName")]</c> — this eliminates the typo-class
/// bug where <c>[Authorize(Roles = "Adming")]</c> silently never matches
/// anyone (compiles cleanly, denies every user) because the policy name is
/// declared once here and the role-set is declared once in Program.cs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Razor <c>@attribute</c> directives require string literals</b> — they
/// do NOT support constant references like
/// <c>@attribute [Authorize(Policy = Policies.AdminOnly)]</c>. Pages
/// therefore use the literal string <c>"AdminOnly"</c> with a
/// <c>// Policy: Policies.AdminOnly</c> comment immediately above so grep
/// can cross-check the literal against these constants. See
/// <c>Roles.cs</c> for the matching role-name constants used by
/// <c>RequireRole(Roles.X, ...)</c> calls in <c>Program.cs</c>.
/// </para>
/// <para>
/// <b>SAFETY:</b> Policy names must match a registration in
/// <c>Program.cs</c> (<c>AddAuthorization</c> options → <c>AddPolicy</c>).
/// The Razor compiler does NOT check that the literal string passed to
/// <c>[Authorize(Policy = "...")]</c> corresponds to a registered policy —
/// a typo like <c>[Authorize(Policy = "AdminnOnly")]</c> compiles cleanly
/// and denies every user (the same failure mode this refactor exists to
/// eliminate for role strings). The defense against policy-name typos is
/// the cross-check comment convention above plus a grep audit:
/// <c>grep "@attribute \[Authorize(Policy" TakOne.WebUI</c> returns 24 page
/// sites whose literals must each appear in this file.
/// </para>
/// <para>
/// <b>DO NOT</b> remove the per-handler <c>[RequireRole]</c> attributes on
/// Application-layer CQRS command/query handlers — that is a separate
/// defense-in-depth layer at the CQRS boundary (per-handler, not per-page)
/// and stays in place regardless of the page-level policy.
/// </para>
/// </remarks>
public static class Policies
{
    /// <summary>
    /// Administrator only. Used by the admin-only notifications console
    /// (desktop <c>AdminNotifications.razor</c> + mobile
    /// <c>MobileAdminNotifications.razor</c>).
    /// </summary>
    public const string AdminOnly = "AdminOnly";

    /// <summary>
    /// Administrator + Manager. Used by group/category/user creation pages
    /// that employees must not access (creating customer groups, building
    /// the category tree, creating staff accounts).
    /// </summary>
    public const string StaffManagement = "StaffManagement";

    /// <summary>
    /// Administrator + Manager + Employee. The default staff page policy —
    /// used by every product, user-detail, group-management, and admin-list
    /// page (12 sites).
    /// </summary>
    public const string ProductManagement = "ProductManagement";

    /// <summary>
    /// Administrator + Manager + Employee + ReadOnly. Dashboard analytics —
    /// everyone who is staff (including read-only auditors) can view.
    /// </summary>
    public const string DashboardAccess = "DashboardAccess";

    /// <summary>
    /// Administrator + Manager + Employee + Customer. Shopping-cart page —
    /// staff can preview a customer's cart, and customers shop for
    /// themselves.
    /// </summary>
    public const string CartAccess = "CartAccess";
}
