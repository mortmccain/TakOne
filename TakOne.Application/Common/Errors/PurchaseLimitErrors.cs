using System.Globalization;

namespace TakOne.Application.Common.Errors;

/// <summary>
/// Produces and parses culture-neutral error strings for the
/// "purchase limit exceeded" failure mode.
///
/// WHY THIS EXISTS:
///   Five handlers can return a purchase-limit-exceeded failure
///   (CreateOrAppendSale, AddItemToSale, UpdateSaleLineItem,
///   QuickReorderLastSale, SubmitSale). Originally each one built its
///   own hardcoded Persian message — which (a) was always in Persian
///   even when the UI culture was English, and (b) leaked the internal
///   "group" concept to customers ("سهمیه گروه مشتری...").
///
///   This helper produces a STABLE, CULTURE-NEUTRAL error string of the
///   form: <c>PurchaseLimitExceeded:{productName}|{limit}</c>. The UI
///   layer (Products / Cart / SaleDetail pages) intercepts this with
///   <see cref="TryParse"/> and substitutes a properly localized
///   message via IStringLocalizer — without mentioning "groups".
///
/// FORMAT:
///   <c>PurchaseLimitExceeded:{productName}|{limit}</c>
///   where {productName} may contain any character (including '|'
///   itself — we split on the LAST '|', so a product name with a pipe
///   in it still parses correctly), and {limit} is an integer.
///
///   Example: <c>PurchaseLimitExceeded:اسپاگتی 1.2|2</c>
///
/// USAGE IN A HANDLER:
///   <code>
///   return Result.Failure(PurchaseLimitErrors.Format(product.Name, purchaseLimit.Value));
///   </code>
///
/// USAGE IN A PAGE:
///   <code>
///   if (PurchaseLimitErrors.TryParse(result.Error, out var name, out var limit))
///       await Toast.Error(string.Format(Loc["PurchaseLimitExceeded"], name, limit));
///   else
///       await Toast.Error(result.Error);
///   </code>
/// </summary>
public static class PurchaseLimitErrors
{
    /// <summary>
    /// The stable prefix that identifies a purchase-limit-exceeded
    /// error string. The UI uses this to detect the error type without
    /// having to parse Persian / English substrings.
    /// </summary>
    public const string Prefix = "PurchaseLimitExceeded:";

    /// <summary>
    /// Builds a culture-neutral error string for "purchase limit
    /// exceeded on {productName}, the limit is {limit} units".
    /// The UI layer localizes this into the user's language.
    /// </summary>
    public static string Format(string productName, int limit)
        // InvariantCulture — see SalaryBudgetExceededErrors.Format for the
        // rationale (culture-neutral wire format; fa-IR renders Persian digits).
        => $"{Prefix}{productName}|{limit.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Tries to parse a purchase-limit-exceeded error string back into
    /// its (productName, limit) components. Returns false if the string
    /// is not a purchase-limit error (e.g. a generic "stock check
    /// failed" message — the UI should fall back to displaying the
    /// raw error in that case).
    /// </summary>
    public static bool TryParse(string? error, out string productName, out int limit)
    {
        productName = string.Empty;
        limit = 0;

        if (string.IsNullOrEmpty(error)
            || !error.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = error[Prefix.Length..];

        // Split on the LAST '|' so a product name that itself contains
        // '|' still parses correctly. (Product names with '|' are
        // unlikely but defensively correct.)
        var i = rest.LastIndexOf('|');
        if (i < 0) return false;

        productName = rest[..i];
        return int.TryParse(rest[(i + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out limit);
    }
}