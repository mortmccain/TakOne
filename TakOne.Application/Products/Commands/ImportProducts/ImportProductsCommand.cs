using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Products.Commands.ImportProducts;

/// <summary>
/// One parsed data row of a product-import CSV. The WebUI layer parses the
/// CSV file (RFC 4180) and maps each data row onto this DTO; the command
/// handler owns all database-side validation + the create/update decision.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY A DTO BAG INSTEAD OF RAW CSV STRINGS</b>: the Application layer
/// must not know about CSV syntax (quoting, encoding, header rows) — that's
/// a presentation/transport concern owned by the WebUI's
/// <c>CsvParser</c>. Handing the handler pre-parsed primitives keeps the
/// handler testable with plain records and keeps RFC 4180 edge cases in
/// exactly one place.
/// </para>
/// <para>
/// <b>COLUMN MAPPING</b> (positional — mirrors the AdminProducts export's
/// fixed column order): Name, Category, SubCategory, SubSubCategory, Price,
/// Currency, Stock, Status, Picture directory, Picture file name. The
/// Status column is deliberately NOT part of this DTO — stock status is
/// derived from <c>StockQuantity</c> in the domain, so importing it would
/// be redundant (and a source of contradictions). The Picture DIRECTORY is
/// likewise informational only: the canonical picture URL is always
/// reconstructed from the file name (see the handler's remarks).
/// </para>
/// <para>
/// <b>DESCRIPTION GAP</b>: the export format does not carry the product
/// description (it mirrors the admin grid's visible columns). On CREATE the
/// handler seeds the description with the product name (the domain requires
/// a non-empty description — see <c>Product.EnsureDescriptionValid</c>);
/// on UPDATE the existing description is preserved untouched.
/// </para>
/// </remarks>
public sealed record ProductImportRowDto
{
    /// <summary>
    /// The product's catalog name. Unique across the catalog — a row whose
    /// name matches an existing product triggers an UPDATE, not a create.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Display name of the product's top-level Category, as written by the
    /// export (possibly suffixed with a "(deactivated)" marker — the
    /// handler strips it when resolving).
    /// </summary>
    public string CategoryName { get; init; } = string.Empty;

    /// <summary>
    /// Display name of the product's SubCategory, or empty when the product
    /// has no subcategory. Also may carry a "(deactivated)" suffix.
    /// </summary>
    public string SubCategoryName { get; init; } = string.Empty;

    /// <summary>
    /// Display name of the product's SubSubCategory, or empty when the
    /// product has no sub-subcategory. Also may carry a
    /// "(deactivated)" suffix.
    /// </summary>
    public string SubSubCategoryName { get; init; } = string.Empty;

    /// <summary>
    /// Raw price amount as parsed from the CSV (invariant culture — the
    /// export always writes invariant decimals).
    /// </summary>
    public decimal PriceAmount { get; init; }

    /// <summary>
    /// 3-letter ISO 4217 currency code (e.g. <c>IRR</c>).
    /// </summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>
    /// The product's absolute stock quantity. On UPDATE this REPLACES the
    /// current stock (a restore-style import wants the CSV value, not a
    /// delta).
    /// </summary>
    public int StockQuantity { get; init; }

    /// <summary>
    /// The picture's FILE NAME as exported (e.g. <c>ab12cd34.jpg</c>), or
    /// empty when the product has no picture. The handler reconstructs the
    /// canonical picture URL from this name.
    /// </summary>
    public string? PictureFileName { get; init; }

    /// <summary>
    /// 1-based data-row number in the source CSV (1 = first row under the
    /// header). Carried through to the per-row result so the admin can find
    /// the offending line in the file.
    /// </summary>
    public int RowNumber { get; init; }
}

/// <summary>
/// Bulk-imports (or updates) products from a CSV exported by the
/// AdminProducts page's export button.
///
/// AUTHORIZATION:
///   Admin only. Bulk import is a destructive-capable admin operation —
///   one file can create or overwrite every product's price/stock/picture.
///   The Settings page renders the import card only for admins, and
///   <see cref="AuthorizationMiddleware"/> enforces the role server-side.
///
/// SEMANTICS (UPSERT BY NAME):
///   - Row name NOT in catalog  → CREATE a new product.
///   - Row name already exists  → UPDATE price, stock, category path, and
///                                picture (description preserved — the CSV
///                                format has no description column).
///   - Row invalid (bad category, bad price, …) → row-level failure in the
///     result; every OTHER row still imports (best-effort, per-row report).
///
/// The handler returns a per-row report (created / updated / failed with
/// reason) — never a single opaque error — because a bulk import must be
/// auditable line by line.
/// </summary>
[RequireRoles(Roles.Admin)]
public sealed record ImportProductsCommand
(
    IReadOnlyList<ProductImportRowDto> Rows
);
