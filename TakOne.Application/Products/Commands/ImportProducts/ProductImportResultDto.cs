namespace TakOne.Application.Products.Commands.ImportProducts;

/// <summary>
/// The outcome of one row of a product-import CSV. Rows are reported
/// individually (created / updated / failed) so the admin can audit exactly
/// what the import did — and find the offending line for every failure.
/// </summary>
public sealed record ProductImportRowResultDto
{
    /// <summary>
    /// 1-based data-row number in the source CSV (1 = first row under the
    /// header). Echoed from <c>ProductImportRowDto.RowNumber</c>.
    /// </summary>
    public int RowNumber { get; init; }

    /// <summary>
    /// The product name the row carried (empty when the row's name cell
    /// was empty — the failure message explains what's missing).
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// What the handler did with the row.
    /// </summary>
    public ProductImportRowOutcome Outcome { get; init; }

    /// <summary>
    /// Human-readable detail: the failure reason when
    /// <see cref="Outcome"/> is <see cref="ProductImportRowOutcome.Failed"/>,
    /// otherwise empty.
    /// </summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// What the import handler did with one CSV row.
/// </summary>
public enum ProductImportRowOutcome
{
    /// <summary>
    /// No product with the row's name existed — a new product was created
    /// (with the description seeded from the product name, because the CSV
    /// format carries no description column).
    /// </summary>
    Created = 0,

    /// <summary>
    /// A product with the row's name already existed — its price, stock,
    /// category path, and picture were updated. The existing description
    /// was preserved.
    /// </summary>
    Updated = 1,

    /// <summary>
    /// The row was rejected (unparseable price, unknown category,
    /// invalid currency, …). Nothing was written for this row; every
    /// other row was still processed.
    /// </summary>
    Failed = 2
}

/// <summary>
/// Aggregate outcome of an <c>ImportProductsCommand</c>: the three tally
/// counts plus the per-row report. Deliberately a SUCCESS-shaped result
/// even when some rows failed — partial success is the import's normal,
/// reported mode; only infrastructure failures (DB unreachable) produce a
/// <c>Result.Failure</c> from the handler.
/// </summary>
public sealed record ProductImportResultDto
{
    /// <summary>
    /// How many rows created a brand-new product.
    /// </summary>
    public int CreatedCount { get; init; }

    /// <summary>
    /// How many rows updated an existing product (matched by name).
    /// </summary>
    public int UpdatedCount { get; init; }

    /// <summary>
    /// How many rows were rejected. Always equals
    /// <c>RowResults.Count(r => r.Outcome == Failed)</c>.
    /// </summary>
    public int FailedCount { get; init; }

    /// <summary>
    /// Per-row report in FILE ORDER (row 1 first). Includes the failed
    /// rows with their reasons.
    /// </summary>
    public IReadOnlyList<ProductImportRowResultDto> RowResults { get; init; }
        = Array.Empty<ProductImportRowResultDto>();
}
