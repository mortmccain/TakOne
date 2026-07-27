namespace TakOne.Application.Sales.Queries.GetActiveCartForUser;

/// <summary>
/// Loads the current user's active shopping cart (their Draft Sale, if any)
/// and projects it to <see cref="DTOs.CartDto"/>.
///
/// "Active cart" definition (matches
/// <see cref="TakOne.Application.Common.Interfaces.ISaleRepository.GetActiveDraftForUserAsync"/>):
///   - CustomerId == current user's Id  (the user IS the customer)
///   - Status == Draft
/// If multiple drafts exist (race condition — see the repository method's
/// XML doc), the most recently created one is returned.
///
/// RETURN SEMANTICS:
///   - Success(null)  → the user has no active draft (empty cart). This is
///                      a NORMAL state — the /Cart UI renders an empty-state
///                      panel. Not a failure.
///   - Success(CartDto) → the user has an active draft; the DTO carries the
///                      sale id, total, line items (with live stock per line).
///   - Failure(error) → the load failed for an unexpected reason (auth,
///                      DB timeout, etc.). The error message is user-facing.
///
/// AUTHORIZATION:
///   Any authenticated user can call this query — every role can shop
///   (Admin, Manager, Employee, Customer). The handler resolves the caller
///   from <c>ICurrentUserService</c> and only returns the cart whose
///   CustomerId matches; there's no way to fetch another user's cart
///   through this query. Use <c>GetSaleByIdQuery</c> (with its own auth
///   checks) for staff-side sale inspection.
/// </summary>
public sealed class GetActiveCartForUserQuery
{
    // ----------------------------------------------------------------
    // No fields on the query itself — the handler resolves the caller
    // from ICurrentUserService. This keeps the query free of identity
    // (so it can be safely serialized + dispatched on a bus without
    // the caller being able to impersonate another user) and matches
    // the convention used by GetSaleByIdQuery.
    // ----------------------------------------------------------------
}