namespace TakOne.Application.Common.Errors;

/// <summary>
/// Produces and parses culture-neutral error strings for the
/// "product no longer exists (hard-deleted) while a sale referencing
/// it was being processed" failure mode.
///
/// WHY THIS EXISTS:
///   <c>ApproveSaleCommandHandler</c> batch-loads the products on a sale
///   to pre-check stock. If a product is missing from the batch result it
///   was hard-deleted after the sale was created (products are normally
///   soft-deactivated — hard deletes are an exceptional admin/recovery
///   action). The historical behavior mapped this case to
///   <see cref="CategoryDeactivatedErrors"/> — a MISLEADING message that
///   tells the staff member the category was deactivated when the real
///   problem is the product row is gone. This helper gives the case its
///   own stable, culture-neutral wire format so the UI can localize an
///   accurate message.
///
/// FORMAT:
///   <c>ProductMissing:{productName}</c>
///   Example: <c>ProductMissing:اسپاگتی 1.2</c>
///
/// USAGE IN A HANDLER:
///   <code>
///   return Result.Failure(ProductMissingErrors.Format(line.ProductName));
///   </code>
///
/// USAGE IN A PAGE:
///   <code>
///   if (ProductMissingErrors.TryParse(result.Error, out var name))
///       return string.Format(Loc["ProductMissing"], name);
///   </code>
/// </summary>
public static class ProductMissingErrors
{
    /// <summary>
    /// The stable prefix that identifies a product-missing error string.
    /// The UI uses this to detect the error type without parsing
    /// localized substrings.
    /// </summary>
    public const string Prefix = "ProductMissing:";

    /// <summary>
    /// Builds a culture-neutral error string for "the product
    /// {productName} no longer exists (it was removed after this sale
    /// was created), so the sale cannot be approved".
    /// </summary>
    public static string Format(string productName)
        => $"{Prefix}{productName}";

    /// <summary>
    /// Tries to parse a product-missing error string back into its
    /// productName component. Returns false if the string is not a
    /// product-missing error.
    /// </summary>
    public static bool TryParse(string? error, out string productName)
    {
        productName = string.Empty;

        if (string.IsNullOrEmpty(error)
            || !error.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        productName = error[Prefix.Length..];
        return productName.Length > 0;
    }
}
