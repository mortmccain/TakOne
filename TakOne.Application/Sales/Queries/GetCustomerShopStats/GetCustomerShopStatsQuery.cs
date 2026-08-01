namespace TakOne.Application.Sales.Queries.GetCustomerShopStats;

/// <summary>
/// Loads the 4 numeric shop-page stats for the current caller. See
/// <see cref="CustomerShopStatsDto"/> for what each field means.
///
/// AUTHORIZATION:
///   Any authenticated user can call this — every role can shop. The
///   handler resolves the caller from <c>ICurrentUserService</c> and
///   scopes all sales aggregations to that caller's own sales.
///
/// NO PARAMETERS:
///   The handler pulls the caller's identity from
///   <c>ICurrentUserService</c>. Keeping the query parameter-less means
///   it can't be used to inspect another user's stats (defense-in-depth
///   against impersonation through the bus).
/// </summary>
public sealed class GetCustomerShopStatsQuery
{
}