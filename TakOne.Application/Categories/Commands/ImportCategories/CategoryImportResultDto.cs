namespace TakOne.Application.Categories.Commands.ImportCategories;

/// <summary>
/// One parsed data row of the categories CSV import.
/// See <see cref="ImportCategoriesCommand"/>'s remarks for the
/// positional column contract and the find-or-create semantics.
/// </summary>
public sealed record CategoryImportRowDto
{
    /// <summary>
    /// 1-based DATA-row number in the source file (header = row 0).
    /// Echoed in the per-row report so the admin can jump to the line.
    /// </summary>
    public int RowNumber { get; init; }

    /// <summary>
    /// Top-level Category name (required). Matched case-insensitively
    /// against existing categories after stripping any trailing
    /// deactivated marker; created ACTIVE when missing.
    /// </summary>
    public string CategoryName { get; init; } = string.Empty;

    /// <summary>
    /// SubCategory name (optional — empty cell means the row only
    /// references the top-level Category).
    /// </summary>
    public string? SubCategoryName { get; init; }

    /// <summary>
    /// SubSubCategory name (optional — requires a SubCategory; a
    /// SubSub without a Sub is rejected per-row, mirroring the
    /// products import's rule).
    /// </summary>
    public string? SubSubCategoryName { get; init; }
}

/// <summary>
/// Per-row outcome of the categories import.
/// </summary>
public enum CategoryImportRowOutcome
{
    /// <summary>At least one node in the row's chain was newly created.</summary>
    Created,

    /// <summary>Every node in the row's chain already existed (no-op).</summary>
    Existed,

    /// <summary>The row failed — see the row's Message for the reason.</summary>
    Failed
}

/// <summary>
/// Per-row report entry — the import-results ledger rendered by the
/// Settings card and downloadable as a CSV report (same shape as the
/// product/user import reports).
/// </summary>
public sealed record CategoryImportRowResultDto
{
    public int RowNumber { get; init; }
    public string CategoryName { get; init; } = string.Empty;
    public CategoryImportRowOutcome Outcome { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// Aggregate result of one categories import run — tallies + the full
/// per-row ledger (file order).
/// </summary>
public sealed record CategoryImportResultDto
{
    public int CreatedCount { get; init; }
    public int ExistedCount { get; init; }
    public int FailedCount { get; init; }
    public IReadOnlyList<CategoryImportRowResultDto> RowResults { get; init; } = Array.Empty<CategoryImportRowResultDto>();
}
