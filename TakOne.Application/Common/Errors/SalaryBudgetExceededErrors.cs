namespace TakOne.Application.Common.Errors;

/// <summary>
/// Produces and parses culture-neutral error strings for the
/// "salary budget exceeded" failure mode.
///
/// WHY THIS EXISTS:
///   When the system's LimitMode is SalaryOnly or Both, the customer's
///   monthly salary budget is enforced on every cart mutation. If the
///   mutation would push the consumed amount over the salary, the
///   handler returns this error.
///
///   The error string is CULTURE-NEUTRAL — the UI layer intercepts it
///   with <see cref="TryParse"/> and substitutes a properly localized
///   message via IStringLocalizer. The customer NEVER sees the word
///   "group" — the localized message is generic, like "Adding this item
///   would exceed your monthly budget. You have {remaining} {currency}
///   remaining."
///
/// FORMAT:
///   <c>SalaryBudgetExceeded:{productName}|{lineTotal}|{remainingBudget}|{currency}</c>
///   Example: <c>SalaryBudgetExceeded:اسپاگتی 1.2|50000|30000|IRR</c>
///
/// USAGE IN A HANDLER:
///   <code>
///   if (budgetInfo.Remaining &lt; lineTotal)
///   {
///       return Result.Failure(
///           SalaryBudgetExceededErrors.Format(product.Name, lineTotal, budgetInfo.Remaining, budgetInfo.Salary.Currency));
///   }
///   </code>
///
/// USAGE IN A PAGE:
///   <code>
///   if (SalaryBudgetExceededErrors.TryParse(result.Error, out var name, out var total, out var remaining, out var cur))
///       await Toast.Error(string.Format(Loc["SalaryBudgetExceeded"], remaining, cur));
///   else
///       await Toast.Error(result.Error);
///   </code>
/// </summary>
public static class SalaryBudgetExceededErrors
{
    /// <summary>
    /// The stable prefix that identifies a salary-budget-exceeded error
    /// string. The UI uses this to detect the error type.
    /// </summary>
    public const string Prefix = "SalaryBudgetExceeded:";

    /// <summary>
    /// Builds a culture-neutral error string for "adding this line
    /// (lineTotal {currency}) would exceed the remaining monthly budget
    /// (remainingBudget {currency})".
    /// </summary>
    /// <param name="productName">The product being added/updated.</param>
    /// <param name="lineTotal">The total amount of the line being added (unitPrice × qty), in <paramref name="currency"/>.</param>
    /// <param name="remainingBudget">The customer's REMAINING budget (salary − consumed). May be 0 or negative.</param>
    /// <param name="currency">The ISO 4217 currency code (matches both the product's price and the customer's salary — they MUST match for this error to be reachable, because currency matching always applies BEFORE the budget check).</param>
    public static string Format(string productName, decimal lineTotal, decimal remainingBudget, string currency)
        => $"{Prefix}{productName}|{lineTotal}|{remainingBudget}|{currency}";

    /// <summary>
    /// Tries to parse a salary-budget-exceeded error string back into its
    /// (productName, lineTotal, remainingBudget, currency) components.
    /// Returns false if the string is not a salary-budget error.
    /// </summary>
    public static bool TryParse(
        string? error,
        out string productName,
        out decimal lineTotal,
        out decimal remainingBudget,
        out string currency)
    {
        productName = string.Empty;
        lineTotal = 0m;
        remainingBudget = 0m;
        currency = string.Empty;

        if (string.IsNullOrEmpty(error)
            || !error.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = error[Prefix.Length..];

        // Format: {productName}|{lineTotal}|{remainingBudget}|{currency}
        // productName may contain '|' — split from the right (last 3 '|').
        var parts = rest.Split('|');
        if (parts.Length < 4) return false;

        // currency is the last part, remainingBudget is the third-to-last,
        // lineTotal is the second-to-last, productName is everything before.
        currency = parts[^1];
        if (!decimal.TryParse(parts[^2], out remainingBudget)) return false;
        if (!decimal.TryParse(parts[^3], out lineTotal)) return false;
        productName = string.Join('|', parts, 0, parts.Length - 3);
        return true;
    }
}