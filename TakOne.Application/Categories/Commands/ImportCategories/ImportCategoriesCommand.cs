using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Categories.Commands.ImportCategories;

/// <summary>
/// Upserts the category tree from a CSV exported by the AdminCategories
/// page's export button (or built by hand from the downloadable
/// template). One command = one import run = a per-row report.
///
/// ROW SEMANTICS (find-or-create at every level, idempotent):
///   Each row names a Category, optionally a SubCategory under it, and
///   optionally a SubSubCategory under THAT. Missing nodes are CREATED
///   (via the <c>Category</c> aggregate's factory/behavior methods);
///   existing nodes are matched case-insensitively by name (with the
///   export's deactivated markers stripped first) and left untouched —
///   re-importing the same file is a no-op that reports "already
///   existed" for every row.
///
/// COLUMN ORDER (positional — mirrors the AdminCategories export):
///   Category, SubCategory, SubSubCategory, Status. The Status
///   column (active/deactivated) is informational ONLY — the import
///   does NOT change activation state; a deactivated node matched by
///   name stays deactivated, a node missing from the DB is created
///   ACTIVE. The deactivated MARKERS the export appends to names are
///   stripped for matching, so a round-tripped file matches its own
///   tree.
/// </summary>
/// <remarks>
/// <para>
/// <b>AUTHORIZATION</b>: <c>[RequireRoles(Admin)]</c> — the categories
/// import is an admin-only capability (matching the export button's
/// admin-only gate on the AdminCategories page). Managers can VIEW and
/// edit the tree page itself, but bulk tree import is reserved to
/// admins per the owner's policy.
/// </para>
/// <para>
/// <b>WEBUI PARSING</b>: the Settings page parses the CSV text with
/// <c>CsvParser</c> into these typed rows before dispatching — exactly
/// the ImportProductsCommand contract (RFC 4180 edge cases stay in one
/// place; the handler is testable with plain records).
/// </para>
/// <para>
/// <b>ACTIVATION-STATE GAP (deliberate)</b>: the format carries the
/// state INFORMATIONALLY (the Status column) but never APPLIES it —
/// activation changes stay interactive (one deliberate click per node
/// on the AdminCategories page), the same policy as the products
/// import's Status column. A re-import therefore never silently
/// resurrects a deliberately deactivated branch.
/// </para>
/// </remarks>
[RequireRoles(Roles.Admin)]
public sealed record ImportCategoriesCommand(IReadOnlyList<CategoryImportRowDto> Rows);
