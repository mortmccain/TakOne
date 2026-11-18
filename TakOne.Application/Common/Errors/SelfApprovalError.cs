namespace TakOne.Application.Common.Errors;

/// <summary>
/// Produces and parses culture-neutral error strings for the
/// "self-approval" failure mode.
///
/// WHY THIS EXISTS:
///   <see cref="TakOne.Application.Sales.Commands.ApproveSale.ApproveSaleCommandHandler"/>
///   must reject any attempt by a staff member (Employee, Manager, or
///   Admin — whoever holds confirmation rights) to approve a sale in
///   which THEY are the customer. Separation of duties: the person who
///   placed an order can never be the one who confirms it — a second
///   person with confirmation rights must do that.
///
///   The sale is "their own" when <c>sale.CustomerId == currentUser.
///   UserId</c> — the buyer of record. This covers BOTH purchase
///   shapes: self-serve shop buys (where the user is customer AND
///   creator) and on-behalf drafts (where staff created the sale for
///   the customer — confirming THAT sale is the customer's own only
///   if the staff member IS the customer, which the same check
///   covers). A staff member confirming a sale they created FOR
///   someone else is NOT blocked — they are not the buyer.
///
///   This helper produces a STABLE, CULTURE-NEUTRAL error string of
///   the form: <c>SelfApproval</c>. The UI layer (SaleDetail /
///   MobileSaleDetail pages) intercepts this with
///   <see cref="TryParse"/> and substitutes a properly localized
///   message via <c>IStringLocalizer</c>.
///
/// FORMAT:
///   <c>SelfApproval</c> (no payload — the error is not parameterized;
///   the staff-facing message is always the same regardless of who
///   triggers it).
///
/// USAGE IN A HANDLER:
///   <code>
///   if (sale.CustomerId == currentUser.UserId)
///   {
///       return Result.Failure(SelfApprovalErrors.Format());
///   }
///   </code>
///
/// USAGE IN A PAGE:
///   <code>
///   if (SelfApprovalErrors.TryParse(result.Error))
///       await Toast.Error(Loc["SelfApprovalBlocked"]);
///   else
///       await Toast.Error(result.Error);
///   </code>
/// </summary>
public static class SelfApprovalErrors
{
    /// <summary>
    /// The stable prefix that identifies a self-approval error string.
    /// The UI uses this to detect the error type without parsing
    /// Persian / English substrings.
    /// </summary>
    public const string Prefix = "SelfApproval";

    /// <summary>
    /// Builds a culture-neutral error string for "the current user is
    /// the customer on this sale and therefore cannot approve it".
    /// The UI layer localizes this into the user's language.
    /// </summary>
    public static string Format()
        => Prefix;

    /// <summary>
    /// Tries to detect a self-approval error string. Returns false if
    /// the string is not this kind of error (e.g. a stock-check,
    /// purchase-limit, or category-deactivation message — the UI
    /// should fall back to other LocalizeError branches or display
    /// the raw error).
    /// </summary>
    public static bool TryParse(string? error)
    {
        return !string.IsNullOrEmpty(error)
            && error == Prefix;
    }
}
