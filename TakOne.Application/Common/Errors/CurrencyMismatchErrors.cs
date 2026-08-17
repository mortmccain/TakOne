namespace TakOne.Application.Common.Errors;

/// <summary>
/// Produces and parses culture-neutral error strings for the
/// "currency mismatch" failure mode.
///
/// WHY THIS EXISTS:
///   Currency matching ALWAYS applies (regardless of LimitMode). A
///   customer whose group's salary is in IRR cannot buy a product priced
///   in USD. The 5 sale-mutating handlers (Step 4 wiring) check this
///   BEFORE adding/updating a line and return this error on mismatch.
///
///   The error string is CULTURE-NEUTRAL — the UI layer (Products /
///   Cart / SaleDetail / ProductDetail pages) intercepts it with
///   <see cref="TryParse"/> and substitutes a properly localized message
///   via IStringLocalizer. The customer NEVER sees the word "group" —
///   the localized message is generic, like "This item is not available
///   for your account (code: CurrencyMismatch)".
///
/// FORMAT:
///   <c>CurrencyMismatch:{productName}|{productCurrency}|{salaryCurrency}</c>
///   Example: <c>CurrencyMismatch:اسپاگتی 1.2|USD|IRR</c>
///
/// USAGE IN A HANDLER:
///   <code>
///   if (!await policy.IsCurrencyMatchAsync(productId, groupId, ct))
///   {
///       return Result.Failure(
///           CurrencyMismatchErrors.Format(product.Name, product.Price.Currency, salaryCurrency));
///   }
///   </code>
///
/// USAGE IN A PAGE:
///   <code>
///   if (CurrencyMismatchErrors.TryParse(result.Error, out var name, out var prodCur, out var salCur))
///       await Toast.Error(Loc["CurrencyMismatch"]);  // localized, no 'group' word
///   else
///       await Toast.Error(result.Error);
///   </code>
/// </summary>
public static class CurrencyMismatchErrors
{
    /// <summary>
    /// The stable prefix that identifies a currency-mismatch error string.
    /// The UI uses this to detect the error type without parsing
    /// Persian / English substrings.
    /// </summary>
    public const string Prefix = "CurrencyMismatch:";

    /// <summary>
    /// Builds a culture-neutral error string for "the product's currency
    /// ({productCurrency}) does not match the customer's salary currency
    /// ({salaryCurrency})".
    /// </summary>
    public static string Format(string productName, string productCurrency, string salaryCurrency)
        => $"{Prefix}{productName}|{productCurrency}|{salaryCurrency}";

    /// <summary>
    /// Tries to parse a currency-mismatch error string back into its
    /// (productName, productCurrency, salaryCurrency) components.
    /// Returns false if the string is not a currency-mismatch error.
    /// </summary>
    public static bool TryParse(
        string? error,
        out string productName,
        out string productCurrency,
        out string salaryCurrency)
    {
        productName = string.Empty;
        productCurrency = string.Empty;
        salaryCurrency = string.Empty;

        if (string.IsNullOrEmpty(error)
            || !error.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = error[Prefix.Length..];

        // Format: {productName}|{productCurrency}|{salaryCurrency}
        // productName may contain '|' — split from the right (last 2 '|').
        var parts = rest.Split('|');
        if (parts.Length < 3) return false;

        // salaryCurrency is the last part, productCurrency is the second-to-last,
        // productName is everything before that (joined back with '|').
        salaryCurrency = parts[^1];
        productCurrency = parts[^2];
        productName = string.Join('|', parts, 0, parts.Length - 2);
        return true;
    }
}