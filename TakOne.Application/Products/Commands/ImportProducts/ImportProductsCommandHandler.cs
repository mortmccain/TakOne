using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Categories.Entities;
using TakOne.Domain.Products.Entities;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Application.Products.Commands.ImportProducts;

/// <summary>
/// Upserts products from a CSV exported by the AdminProducts page
/// (create-by-name / update-by-name — see the command's remarks).
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
/// <remarks>
/// <para>
/// <b>BEST-EFFORT, PER-ROW REPORT</b>: an invalid row fails ITSELF (with a
/// reason in the result) but never aborts the other rows. All domain
/// mutations run through the <c>Product</c> aggregate's guard methods,
/// which validate BEFORE mutating — so a throwing row leaves no half-set
/// tracked entity behind, and the single
/// <see cref="IUnitOfWork.SaveChangesAsync"/> at the end commits exactly
/// the rows that succeeded.
/// </para>
/// <para>
/// <b>CATEGORY RESOLUTION</b>: the CSV carries category NAMES (the export
/// mirrors the admin grid's visible columns), so the handler loads the full
/// category tree ONCE and matches names case-insensitively. The export
/// suffixes deactivated categories with a marker ("(deactivated)" in the
/// English export, "(غیرفعال)" in the Persian one) — the matcher strips a
/// trailing marker before comparing so an exported file re-imports
/// cleanly.
/// </para>
/// <para>
/// <b>PICTURE RE-LINKING</b>: the picture URL is reconstructed from the
/// row's file name as <c>/uploads/products/{fileName}</c> — the canonical
/// URL shape <see cref="IFileStorage"/> always returns. The file name is
/// first NORMALIZED through <see cref="PreservedFileNames"/> — the same
/// canonical rule the Settings page's bulk picture upload applies when it
/// lands a file on disk (spaces, <c>&amp;</c>, parentheses, … are stripped
/// to the <c>[A-Za-z0-9._-]</c> allowlist). Without this, a CSV carrying
/// the ORIGINAL photo names (e.g. <c>"Pizza flour 500g.png"</c>) linked
/// URLs that could never resolve: the upload had stored that photo as
/// <c>Pizzaflour500g.png</c>, so every spaced/special-character name
/// rendered without its picture while allowlist-clean names (e.g.
/// <c>ABC.png</c>) worked. A name that normalizes to nothing usable (no
/// allowlist characters at all) links no picture — such a file could never
/// have been uploaded, so there is nothing on disk to link. The uploaded
/// picture FILES themselves are restored separately (the Settings page's
/// bulk picture upload), which is what makes the CSV + files pair a
/// complete catalog backup.
/// </para>
/// <para>
/// <b>DDD NOTES</b>: every mutation goes through the aggregate —
/// <c>Product.Create</c> for new rows, <c>UpdateDetails</c> /
/// <c>UpdateCategory</c> / <c>SetStock</c> for existing ones. The handler
/// never sets state directly. Cross-aggregate rules (sub belongs to
/// category) are enforced here, exactly like
/// <c>CreateProductCommandHandler</c> does, because the Product aggregate
/// cannot see the Category aggregate.
/// </para>
/// </remarks>
public sealed class ImportProductsCommandHandler
{
    /// <summary>
    /// Hard cap on rows per import call — same value as the export's row
    /// cap, so a round-tripped file can never exceed what the handler will
    /// accept.
    /// </summary>
    private const int MaxRowsPerImport = 2000;

    /// <summary>
    /// Deactivation markers the export appends to category names. The
    /// matcher strips a TRAILING occurrence of either variant before
    /// comparing names. Kept in sync with AdminProducts.en-US.resx /
    /// .fa-IR.resx ("DeactivatedSuffix").
    /// </summary>
    private static readonly string[] DeactivatedSuffixes =
    {
        " (deactivated)",
        " (غیرفعال)"
    };

    public static async Task<Result<ProductImportResultDto>> HandleAsync
        (
        ImportProductsCommand command,
        ICurrentUserService currentUser,
        IProductRepository productRepository,
        ICategoryRepository categoryRepository,
        IUnitOfWork unitOfWork,
        ILogger<ImportProductsCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check. [RequireRoles(Admin)] already rejected
        //    non-admin callers via AuthorizationMiddleware; re-checking
        //    keeps the invariant honest for non-HTTP invocations.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("ImportProducts: unauthenticated call rejected.");
            return Result<ProductImportResultDto>.Failure("Authentication required.");
        }

        if (!currentUser.IsInRole(Roles.Admin))
        {
            logger.LogWarning
                ("ImportProducts: caller {ActorId} is not an Admin. Rejected.", currentUser.UserId);
            return Result<ProductImportResultDto>.Failure("Only administrators may import products.");
        }

        // ------------------------------------------------------------------
        // 1. Shape check. An empty/gigantic file is a caller error, not a
        //    per-row error — fail the whole command with a clear message.
        // ------------------------------------------------------------------
        if (command.Rows.Count == 0)
        {
            return Result<ProductImportResultDto>.Failure("The import file contains no data rows.");
        }

        if (command.Rows.Count > MaxRowsPerImport)
        {
            return Result<ProductImportResultDto>.Failure
                ($"The import file contains {command.Rows.Count} rows — the maximum is {MaxRowsPerImport}. Split the file and retry.");
        }

        // ------------------------------------------------------------------
        // 2. Load the full category tree ONCE. The tree is small (tens of
        //    nodes) and every row needs name → Id resolution against it.
        // ------------------------------------------------------------------
        var categories = await categoryRepository.GetAllAsync(cancellationToken);

        // ------------------------------------------------------------------
        // 3. Process rows. Each row independently resolves its category
        //    path, then creates or updates its product. Domain guard
        //    failures throw BEFORE mutating, so a failed row leaves the
        //    change tracker clean for the rows that follow.
        // ------------------------------------------------------------------
        var rowResults = new List<ProductImportRowResultDto>(command.Rows.Count);
        var created = 0;
        var updated = 0;
        var failed = 0;

        foreach (var row in command.Rows)
        {
            var result = await ProcessRowAsync(row, categories, productRepository, cancellationToken);
            rowResults.Add(result.Result);

            if (result.Outcome == ProductImportRowOutcome.Created) created++;
            else if (result.Outcome == ProductImportRowOutcome.Updated) updated++;
            else failed++;
        }

        // ------------------------------------------------------------------
        // 4. Persist everything that succeeded in ONE SaveChanges (single
        //    transaction — Wolverine's unit-of-work middleware commits it).
        // ------------------------------------------------------------------
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("ImportProducts: {Created} created, {Updated} updated, {Failed} failed (requested by user {ActorId}).",
            created, updated, failed, currentUser.UserId);

        return Result<ProductImportResultDto>.Success(new ProductImportResultDto
        {
            CreatedCount = created,
            UpdatedCount = updated,
            FailedCount = failed,
            RowResults = rowResults
        });
    }

    /// <summary>
    /// Resolves + applies one row. Returns the row report plus the outcome
    /// enum (the enum is what the tallies switch on; the DTO carries it
    /// too, but reading the tuple member avoids re-mapping).
    /// </summary>
    private static async Task<(ProductImportRowResultDto Result, ProductImportRowOutcome Outcome)> ProcessRowAsync
        (
        ProductImportRowDto row,
        List<Category> categories,
        IProductRepository productRepository,
        CancellationToken cancellationToken
        )
    {
        // ── Per-row shape checks (cheap, no DB, no domain call). ──
        if (string.IsNullOrWhiteSpace(row.Name))
        {
            return (Failed(row, "Product name is empty."), ProductImportRowOutcome.Failed);
        }

        if (string.IsNullOrWhiteSpace(row.CategoryName))
        {
            return (Failed(row, "Category is empty."), ProductImportRowOutcome.Failed);
        }

        if (row.StockQuantity < 0)
        {
            return (Failed(row, "Stock quantity cannot be negative."), ProductImportRowOutcome.Failed);
        }

        // ── Category path resolution (name → Id, marker-stripped,
        //    case-insensitive; sub must belong to sub-sub's parent). ──
        if (!TryResolveCategoryPath(categories, row, out var categoryId, out var subCategoryId, out var subSubCategoryId, out var categoryError))
        {
            return (Failed(row, categoryError), ProductImportRowOutcome.Failed);
        }

        try
        {
            // ── Money construction — the value object validates amount > 0
            //    and the 3-letter currency code itself. ──
            var price = new Money(row.PriceAmount, row.Currency);

            // ── Picture URL reconstruction from the exported file name. ──
            var pictureUrl = BuildPictureUrl(row.PictureFileName);

            var existing = await productRepository.GetByNameAsync(row.Name, cancellationToken);

            if (existing is null)
            {
                // CREATE. The CSV format has no description column and the
                // domain requires a non-empty description — seed it with
                // the product name (documented on the command).
                var product = Product.Create
                    (
                    row.Name,
                    row.Name, // description placeholder — see command remarks
                    price,
                    row.StockQuantity,
                    categoryId,
                    pictureUrl,
                    subCategoryId,
                    subSubCategoryId
                    );

                await productRepository.AddAsync(product, cancellationToken);

                return (new ProductImportRowResultDto
                {
                    RowNumber = row.RowNumber,
                    Name = row.Name,
                    Outcome = ProductImportRowOutcome.Created
                }, ProductImportRowOutcome.Created);
            }

            // UPDATE — price, picture (UpdateDetails), category path
            // (UpdateCategory), stock (SetStock). The description is NOT
            // in the CSV: preserve the existing one.
            existing.UpdateDetails(row.Name, existing.Description, price, pictureUrl);
            existing.UpdateCategory(categoryId, subCategoryId, subSubCategoryId);
            existing.SetStock(row.StockQuantity);

            return (new ProductImportRowResultDto
            {
                RowNumber = row.RowNumber,
                Name = row.Name,
                Outcome = ProductImportRowOutcome.Updated
            }, ProductImportRowOutcome.Updated);
        }
        catch (DomainException ex)
        {
            // A guard threw BEFORE mutating — nothing was tracked for this
            // row; the remaining rows continue.
            return (Failed(row, ex.Message), ProductImportRowOutcome.Failed);
        }
    }

    /// <summary>
    /// Builds the canonical picture URL from the exported file name, or
    /// null when the row carried no picture (an empty cell). The file name
    /// is normalized through <see cref="PreservedFileNames"/> — the SAME
    /// canonical rule the bulk picture upload applies on disk — so the URL
    /// always points at the name the upload actually stored (see the
    /// handler's PICTURE RE-LINKING remarks for the mismatch this prevents).
    /// Only the FILE NAME is honored — the URL prefix is the one
    /// <see cref="IFileStorage"/> always returns, regardless of what
    /// directory the export happened to record (the column is informational
    /// for the human reading the CSV).
    /// </summary>
    private static string? BuildPictureUrl(string? pictureFileName)
        => PreservedFileNames.TryNormalizeClientSuppliedName(pictureFileName, out var normalizedName)
            ? $"/uploads/products/{normalizedName}"
            : null;

    /// <summary>
    /// Resolves the row's category NAME path against the loaded category
    /// tree. Matching is case-insensitive on trimmed names, with the
    /// export's "(deactivated)" markers stripped first. A SubSubCategory
    /// requires its full parent path (mirroring the cross-aggregate rule
    /// <c>CreateProductCommandHandler</c> enforces).
    /// </summary>
    private static bool TryResolveCategoryPath
        (
        List<Category> categories,
        ProductImportRowDto row,
        out Guid categoryId,
        out Guid? subCategoryId,
        out Guid? subSubCategoryId,
        out string error
        )
    {
        categoryId = Guid.Empty;
        subCategoryId = null;
        subSubCategoryId = null;
        error = string.Empty;

        var category = categories.FirstOrDefault
            (c => NamesEqual(c.Name, row.CategoryName));

        if (category is null)
        {
            error = $"Category '{row.CategoryName}' was not found.";
            return false;
        }

        categoryId = category.Id;

        if (string.IsNullOrWhiteSpace(row.SubCategoryName))
        {
            // A SubSub without a Sub is a contradiction — reject early
            // (the domain's EnsureSubCategoryConsistency would throw the
            // same rule; surfacing it here gives the row a clearer message).
            if (!string.IsNullOrWhiteSpace(row.SubSubCategoryName))
            {
                error = "A sub-sub-category was given without a sub-category.";
                return false;
            }

            return true;
        }

        var subCategory = category.SubCategories.FirstOrDefault
            (s => NamesEqual(s.Name, row.SubCategoryName));

        if (subCategory is null)
        {
            error = $"Sub-category '{row.SubCategoryName}' was not found under category '{category.Name}'.";
            return false;
        }

        subCategoryId = subCategory.Id;

        if (string.IsNullOrWhiteSpace(row.SubSubCategoryName))
        {
            return true;
        }

        var subSubCategory = subCategory.SubSubCategories.FirstOrDefault
            (ss => NamesEqual(ss.Name, row.SubSubCategoryName));

        if (subSubCategory is null)
        {
            error = $"Sub-sub-category '{row.SubSubCategoryName}' was not found under sub-category '{subCategory.Name}'.";
            return false;
        }

        subSubCategoryId = subSubCategory.Id;
        return true;
    }

    /// <summary>
    /// Case-insensitive, trimmed name equality after stripping a trailing
    /// "(deactivated)" marker from the CSV-side name (the export appends
    /// the marker to deactivated categories — see DeactivatedSuffixes).
    /// The DATABASE-side name is never modified.
    /// </summary>
    private static bool NamesEqual(string databaseName, string csvName)
    {
        var normalized = csvName?.Trim() ?? string.Empty;

        foreach (var suffix in DeactivatedSuffixes)
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^suffix.Length].TrimEnd();
                break; // at most one marker is ever appended
            }
        }

        return string.Equals(databaseName?.Trim(), normalized, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a Failed row report. Prefixes the message with the row's
    /// 1-based line so the admin can jump straight to the offending line
    /// in the CSV.
    /// </summary>
    private static ProductImportRowResultDto Failed(ProductImportRowDto row, string message)
        => new()
        {
            RowNumber = row.RowNumber,
            Name = row.Name,
            Outcome = ProductImportRowOutcome.Failed,
            Message = message
        };
}
