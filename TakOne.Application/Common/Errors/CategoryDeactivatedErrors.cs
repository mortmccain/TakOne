namespace TakOne.Application.Common.Errors;

/// <summary>
/// Produces and parses culture-neutral error strings for the
/// "product's category (or sub / sub-sub) is deactivated" failure mode.
///
/// WHY THIS EXISTS:
///   Four handlers can return a category-deactivated failure
///   (CreateOrAppendSale, AddItemToSale, UpdateSaleLineItem,
///   SubmitSale). Without this helper they would each have to build
///   their own message — which historically meant a hardcoded Persian
///   string (see the PurchaseLimitErrors class doc for the same bug
///   pattern).
///
///   Business rule: when a Category / SubCategory / SubSubCategory is
///   deactivated, every product under it MUST be unbuyable but its
///   StockQuantity is preserved as-is (deactivation is NOT the same
///   as zeroing stock). The customer-facing Products page also hides
///   such products entirely (filtered in GetProductsPaginatedQueryHandler).
///
///   This helper produces a STABLE, CULTURE-NEUTRAL error string of the
///   form: <c>CategoryDeactivated:{productName}</c>. The UI layer
///   (Products / Cart / SaleDetail pages) intercepts this with
///   <see cref="TryParse"/> and substitutes a properly localized
///   message via IStringLocalizer.
///
/// FORMAT:
///   <c>CategoryDeactivated:{productName}</c>
///   where {productName} may contain any character (including ':'
///   itself — we only split on the FIRST ':', so a product name with
///   a colon in it still parses correctly).
///
///   Example: <c>CategoryDeactivated:اسپاگتی 1.2</c>
///
/// USAGE IN A HANDLER:
///   <code>
///   return Result.Failure(CategoryDeactivatedErrors.Format(product.Name));
///   </code>
///
/// USAGE IN A PAGE:
///   <code>
///   if (CategoryDeactivatedErrors.TryParse(result.Error, out var name))
///       await Toast.Error(string.Format(Loc["CategoryDeactivated"], name));
///   else
///       await Toast.Error(result.Error);
///   </code>
/// </summary>
public static class CategoryDeactivatedErrors
{
    /// <summary>
    /// The stable prefix that identifies a category-deactivated error
    /// string. The UI uses this to detect the error type without having
    /// to parse Persian / English substrings.
    /// </summary>
    public const string Prefix = "CategoryDeactivated:";

    /// <summary>
    /// Builds a culture-neutral error string for "the category of
    /// {productName} has been deactivated, so this product is no
    /// longer available for purchase". The UI layer localizes this
    /// into the user's language.
    /// </summary>
    public static string Format(string productName)
        => $"{Prefix}{productName}";

    /// <summary>
    /// Tries to parse a category-deactivated error string back into
    /// its productName component. Returns false if the string is not
    /// a category-deactivated error (e.g. a generic stock-check or
    /// purchase-limit message — the UI should fall back to other
    /// LocalizeError branches or display the raw error).
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