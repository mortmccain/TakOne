namespace TakOne.Application.Common.Errors;

/// <summary>
/// Produces and parses culture-neutral error strings for the
/// "not enough stock to approve/fulfil a sale" failure mode.
///
/// WHY THIS EXISTS:
///   <see cref="Sales.Commands.ApproveSale.ApproveSaleCommandHandler"/>
///   originally returned a hardcoded English string:
///   <c>"Not enough stock to approve sale. Product 'X' has 0 in stock,
///   but the sale requires 4."</c>
///   That message was always in English even when the UI culture was
///   Persian (fa-IR), which leaked as an English error toast in Persian
///   mode — the exact bug reported by the user.
///
///   This helper produces a STABLE, CULTURE-NEUTRAL error string of the
///   form: <c>StockExceeded:{productName}|{stock}|{required}</c>. The UI
///   layer (SaleDetail page) intercepts this with <see cref="TryParse"/>
///   and substitutes a properly localized message via IStringLocalizer.
///
///   This mirrors the established pattern in
///   <see cref="PurchaseLimitErrors"/> and
///   <see cref="CategoryDeactivatedErrors"/>.
///
/// FORMAT:
///   <c>StockExceeded:{productName}|{stock}|{required}</c>
///   where {productName} may contain any character (including '|' — we
///   split on the LAST '|' and the SECOND-TO-LAST '|', so a product name
///   with pipes in it still parses correctly), {stock} is an integer
///   (current available stock), and {required} is an integer (total
///   quantity the sale needs).
///
///   Example: <c>StockExceeded:اسپاگتی 1.5|0|4</c>
///
/// USAGE IN A HANDLER:
///   <code>
///   return Result.Failure(StockErrors.Format(product.Name, product.StockQuantity, totalQuantityForProduct));
///   </code>
///
/// USAGE IN A PAGE:
///   <code>
///   if (StockErrors.TryParse(result.Error, out var name, out var stock, out var required))
///       await Toast.Error(string.Format(Loc["StockExceeded"], name, stock, required));
///   else
///       await Toast.Error(result.Error);
///   </code>
/// </summary>
public static class StockErrors
{
    /// <summary>
    /// The stable prefix that identifies a stock-exceeded error string.
    /// The UI uses this to detect the error type without having to parse
    /// Persian / English substrings.
    /// </summary>
    public const string Prefix = "StockExceeded:";

    /// <summary>
    /// Builds a culture-neutral error string for "not enough stock of
    /// {productName}: currently {stock} in stock, but the sale requires
    /// {required}". The UI layer localizes this into the user's language.
    /// </summary>
    public static string Format(string productName, int stock, int required)
        => $"{Prefix}{productName}|{stock}|{required}";

    /// <summary>
    /// Tries to parse a stock-exceeded error string back into its
    /// (productName, stock, required) components. Returns false if the
    /// string is not a stock-exceeded error (e.g. a generic message or
    /// a purchase-limit error — the UI should fall back to other
    /// LocalizeError branches or display the raw error).
    /// </summary>
    public static bool TryParse(string? error, out string productName, out int stock, out int required)
    {
        productName = string.Empty;
        stock = 0;
        required = 0;

        if (string.IsNullOrEmpty(error)
            || !error.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = error[Prefix.Length..];

        // Split on the LAST '|' to separate {required} from the rest.
        var lastPipe = rest.LastIndexOf('|');
        if (lastPipe < 0) return false;

        if (!int.TryParse(rest[(lastPipe + 1)..], out required))
            return false;

        rest = rest[..lastPipe];

        // Split the remaining on the LAST '|' again to separate {stock}
        // from {productName}. This way a product name that itself contains
        // '|' still parses correctly.
        var secondPipe = rest.LastIndexOf('|');
        if (secondPipe < 0) return false;

        if (!int.TryParse(rest[(secondPipe + 1)..], out stock))
            return false;

        productName = rest[..secondPipe];
        return productName.Length > 0;
    }
}