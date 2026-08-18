using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Customers.Commands.BulkApplyDefaultsForGroup;

/// <summary>
/// Bulk-applies the default per-group purchase limit
/// (<see cref="TakOne.Domain.Products.ValueObjects.CustomerGroupPurchaseLimit.DefaultLimit"/>,
/// currently 1) to every Product in the catalog that does NOT already have
/// a limit row for the given CustomerGroup.
///
/// AUTHORIZATION:
///   Manager, Admin. This is a bulk mutation that can touch every row in
///   the Products table — restrict to senior staff.
///
/// PURPOSE — closes the "reactivation gap" documented since Step 5:
///   When a CustomerGroup is DEACTIVATED and then NEW products are added
///   to the catalog, those new products do NOT receive a default purchase
///   limit for the inactive group (the <c>CreateProductCommandHandler</c>
///   Phase-1 auto-default loop only iterates ACTIVE groups, by design).
///   If the admin later REACTIVATES the group, the reactivated group's
///   customers have NO purchase cap on those new products (null = unlimited)
///   until the admin manually sets limits via Manage Products.
///
///   This command fills that gap in one shot: scan every product, and
///   wherever a limit row for this group is missing, insert one with the
///   default value (1). Products that already have a limit row — whether
///   set by the original CreateGroup bulk-default flow, by an explicit
///   SetProductPurchaseLimit call, or by CreateProduct's Phase-1 loop —
///   are SKIPPED, so admin-set overrides are preserved.
///
/// IDEMPOTENCY:
///   Fully idempotent. Running it twice in a row is a no-op on the second
///   run (every product that was missing a limit row got one on the first
///   run; the second run finds nothing missing and reports zero updates).
///
/// ATOMICITY:
///   The bulk-default loop runs in the SAME Wolverine ambient transaction
///   as the command invocation. If any batch fails (DB connection drops,
///   timeout, etc.), the entire operation rolls back — no partial state.
///   Batches are committed individually via SaveChanges (within the
///   ambient transaction) so the change tracker doesn't grow unbounded
///   on large catalogs.
///
/// GROUP MUST BE ACTIVE:
///   The handler rejects the command if the group is inactive. The use
///   case is "reactivate first, then bulk-apply defaults to fill the gap
///   for products created during the inactive period". Allowing the
///   operation on inactive groups would create limit rows for a group
///   that can't be purchased from anyway — a confusing state. The admin
///   should Activate the group first, then run this command.
/// </summary>
[RequireRoles(Roles.Manager, Roles.Admin)]
public sealed record BulkApplyDefaultsForGroupCommand(Guid GroupId);