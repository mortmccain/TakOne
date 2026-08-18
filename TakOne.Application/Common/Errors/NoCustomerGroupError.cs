namespace TakOne.Application.Common.Errors;

/// <summary>
/// Produces and parses culture-neutral error strings for the
/// "user has no customer group assigned" failure mode.
///
/// WHY THIS EXISTS:
///   Four sale-mutating handlers (CreateOrAppendSale,
///   QuickReorderLastSale, UpdateSaleLineItem, SubmitSale) must reject
///   any purchase attempt by a user who is not assigned to a
///   <c>CustomerGroup</c>. Without a group, the user has no salary
///   budget, no currency constraint, and no per-product cap — i.e.
///   unlimited purchases, which defeats the entire purpose of the
///   salary/budget feature.
///
///   Business rule (Step 12-a runtime fix):
///     Users that belong to no group MUST NOT be able to buy anything.
///     This applies to ALL users (staff included). If staff need to
///     make purchases, they must be assigned to a group first.
///
///   This helper produces a STABLE, CULTURE-NEUTRAL error string of the
///   form: <c>NoCustomerGroup</c>. The UI layer (Products / Cart /
///   SaleDetail / ProductDetail pages) intercepts this with
///   <see cref="TryParse"/> and substitutes a properly localized
///   message via <c>IStringLocalizer</c>. The customer NEVER sees the
///   word "group" in the localized message — the message reads like
///   "Your account is not configured for purchases. Please contact
///   an administrator." so the internal "customer group" concept is
///   not leaked to the end user.
///
/// FORMAT:
///   <c>NoCustomerGroup</c> (no payload — the error is not parameterized;
///   the customer-facing message is always the same generic
///   "contact administrator" string regardless of who triggers it).
///
/// USAGE IN A HANDLER:
///   <code>
///   if (groupId is null)
///   {
///       return Result.Failure(NoCustomerGroupErrors.Format());
///   }
///   </code>
///
/// USAGE IN A PAGE:
///   <code>
///   if (NoCustomerGroupErrors.TryParse(result.Error))
///       await Toast.Error(Loc["NoCustomerGroup"]);
///   else
///       await Toast.Error(result.Error);
///   </code>
/// </summary>
public static class NoCustomerGroupErrors
{
    /// <summary>
    /// The stable prefix that identifies a "no customer group" error
    /// string. The UI uses this to detect the error type without
    /// parsing Persian / English substrings.
    /// </summary>
    public const string Prefix = "NoCustomerGroup";

    /// <summary>
    /// Builds a culture-neutral error string for "the user has no
    /// customer group assigned and therefore cannot make purchases".
    /// The UI layer localizes this into the user's language.
    /// </summary>
    public static string Format()
        => Prefix;

    /// <summary>
    /// Tries to detect a "no customer group" error string. Returns
    /// false if the string is not this kind of error (e.g. a generic
    /// stock-check or purchase-limit message — the UI should fall
    /// back to other LocalizeError branches or display the raw error).
    /// </summary>
    public static bool TryParse(string? error)
    {
        return !string.IsNullOrEmpty(error)
            && error == Prefix;
    }
}