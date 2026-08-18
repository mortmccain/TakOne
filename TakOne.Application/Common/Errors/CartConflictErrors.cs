namespace TakOne.Application.Common.Errors;

/// <summary>
/// Produces and parses culture-neutral error strings for the
/// "cart conflict" failure mode.
///
/// WHY THIS EXISTS:
///   The 4 sale-mutating handlers that operate on an EXISTING draft
///   (AddItemToSale, UpdateSaleLineItem, RemoveSaleLineItem, SubmitSale)
///   acquire a CartMutationLock and then RE-LOAD the sale to confirm it
///   is still a Draft. If a concurrent invocation submitted the draft in
///   the meantime, the re-load returns null or a non-Draft status —
///   the handler treats this as a conflict and returns this error.
///
///   The previous implementation returned a hard-coded English string
///   ("This cart was modified by another session. Refresh the page and
///   try again.") which leaked directly into the UI as an un-localized
///   raw string. This class replaces that with a stable error CODE
///   that the UI layer (Products / Cart / SaleDetail / ProductDetail
///   pages) intercepts via <see cref="TryParse"/> and substitutes with
///   a properly localized message via IStringLocalizer.
///
/// FORMAT:
///   <c>CartConflict:</c>
///   (No parameters — the message is identical regardless of which sale
///   or which user triggered it. The customer just needs to refresh.)
///
/// USAGE IN A HANDLER:
///   <code>
///   if (sale is null || sale.Status != SaleStatus.Draft)
///   {
///       return Result.Failure(CartConflictErrors.Format());
///   }
///   </code>
///
/// USAGE IN A PAGE:
///   <code>
///   if (CartConflictErrors.TryParse(result.Error))
///       await Toast.Error(Loc["CartConflict"]);  // localized
///   else
///       await Toast.Error(result.Error);
///   </code>
/// </summary>
public static class CartConflictErrors
{
    /// <summary>
    /// The stable prefix that identifies a cart-conflict error string.
    /// The UI uses this to detect the error type without parsing
    /// Persian / English substrings.
    /// </summary>
    public const string Prefix = "CartConflict:";

    /// <summary>
    /// Builds a culture-neutral error string for "the cart was modified
    /// by another session". No parameters — the message is the same
    /// regardless of which sale or which user triggered it.
    /// </summary>
    public static string Format() => Prefix;

    /// <summary>
    /// Returns true if the given error string is a cart-conflict error.
    /// No payload to extract — the message is fixed.
    /// </summary>
    public static bool TryParse(string? error)
    {
        return !string.IsNullOrEmpty(error)
            && error.StartsWith(Prefix, StringComparison.Ordinal);
    }
}