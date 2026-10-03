using TakOne.Application.Common.Authorization;
using TakOne.Domain.Users;

namespace TakOne.Application.Users.Commands.ImportUsers;

/// <summary>
/// One parsed data row of a user-import CSV. The WebUI layer parses the CSV
/// file (RFC 4180) AND normalizes the localized display cells (gender and
/// status are written as LOCALIZED labels by the manage-users export) into
/// these culture-neutral, typed fields before dispatching.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THE PAGE NORMALIZES (not the handler)</b>: the exported Gender
/// cell says "Male" in an English export and "مرد" in a Persian one; the
/// Status cell likewise ("Active" / "فعال"). Mapping display text back to
/// enum values requires the SAME localizer that rendered it — a
/// presentation concern the Application layer must not own. The handler
/// receives clean primitives only.
/// </para>
/// <para>
/// <b>COLUMN MAPPING</b> (positional — mirrors the manage-users export's
/// fixed column order): Worker ID, Full name, Gender, Group, Roles
/// (semicolon-joined inside one cell), Status.
/// </para>
/// <para>
/// <b>ROLES</b>: the export writes the user's ASP.NET Identity roles
/// (e.g. <c>"Employee; Customer"</c>) — culture-neutral names, split on
/// semicolons by the page. The handler validates each name against the
/// known role set.
/// </para>
/// <para>
/// <b>DEFAULT PASSWORD</b>: every imported user receives
/// <c>"@" + REVERSED(workerId) + "Temp"</c> (e.g. worker id <c>12345</c>
/// → <c>@54321Temp</c>) and <c>MustChangePassword=true</c> — the same
/// forced one-time change every admin-created account gets — so the
/// importer never knows the user's lasting password.
/// </para>
/// </remarks>
public sealed record UserImportRowDto
{
    /// <summary>
    /// The login identifier (equals the Identity UserName). Rows whose
    /// worker ID already exists are SKIPPED (reported, not failed) so a
    /// re-import is idempotent.
    /// </summary>
    public string WorkerId { get; init; } = string.Empty;

    /// <summary>
    /// The user's display name.
    /// </summary>
    public string FullName { get; init; } = string.Empty;

    /// <summary>
    /// Gender (normalized by the page from the localized cell).
    /// </summary>
    public Gender Gender { get; init; }

    /// <summary>
    /// Customer group NAME, or empty when the user is staff (staff users
    /// never belong to a group). The handler resolves the name to a group
    /// Id against the live groups.
    /// </summary>
    public string GroupName { get; init; } = string.Empty;

    /// <summary>
    /// The Identity roles to assign (normalized names — "Admin",
    /// "Manager", "Employee", "ReadOnly", "Customer"). At least one is
    /// required; the first is the primary role passed to the identity
    /// account creation, the rest are assigned afterwards.
    /// </summary>
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// True = active account; False = created then deactivated (mirrors
    /// the exported Status cell).
    /// </summary>
    public bool IsActive { get; init; }

    /// <summary>
    /// 1-based data-row number in the source CSV (1 = first row under the
    /// header). Echoed into the per-row result for error reporting.
    /// </summary>
    public int RowNumber { get; init; }
}

/// <summary>
/// Bulk-imports users from a CSV exported by the manage-users page.
///
/// AUTHORIZATION:
///   Admin only (the Settings page renders the import card only for
///   admins; <see cref="AuthorizationMiddleware"/> re-enforces it).
///
/// SEMANTICS (CREATE-OR-SKIP):
///   - Worker ID not in the system → CREATE the user (Domain User +
///     ApplicationUser + roles [+ group] + status), with the default
///     password formula.
///   - Worker ID already exists     → SKIP (reported as skipped — a
///     re-import never mutates existing accounts).
///   - Row invalid (unknown role, unknown group, Identity password
///     rejection, …) → row-level failure in the result; other rows still
///     import (best-effort, per-row report).
///
/// The handler returns a per-row report (created / skipped / failed with
/// reason) — the same auditability contract as the product import.
/// </summary>
[RequireRoles(Roles.Admin)]
public sealed record ImportUsersCommand
(
    IReadOnlyList<UserImportRowDto> Rows
);
