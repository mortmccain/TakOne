using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.Primitives;

namespace TakOne.Domain.Products.ValueObjects;

/// <summary>
/// Value object that defines how many units of a specific Product
/// a user in a given customer group is allowed to buy per cart line item.
///
/// Stored as part of the Product aggregate (Product owns a collection of these).
/// Equality is by (GroupId, Limit) — there is no Id, because this is a value object.
///
/// IMMUTABILITY:
///   Value objects are immutable. To "change" a limit, the Product aggregate
///   replaces the old instance with a new one (see Product.SetPurchaseLimit).
///
/// EF CORE MAPPING (Infrastructure):
///   Mapped as an owned collection (OwnsMany) on the Product table.
///   EF will create a shadow primary key in the database; the Domain class
///   has no Id property.
///
/// GROUP REFERENCE:
///   References the <c>CustomerGroup</c> aggregate by <see cref="GroupId"/>
///   (Guid). The previous design used <c>GroupName : string</c>, but
///   that violated referential integrity (no FK, no cascade on rename/delete).
///   The new design uses a proper FK to <c>CustomerGroups.Id</c>.
/// </summary>
public sealed class CustomerGroupPurchaseLimit : BaseValueObject
{



    // ==================================================================================================================================
    //                                                          CONSTANTS
    // ==================================================================================================================================



    /// <summary>
    /// The default per-group limit applied when a new CustomerGroup is
    /// created (bulk-applied to all existing products) OR when a new
    /// Product is created (bulk-applied for each existing active group).
    ///
    /// BUSINESS RULE (Step 5 wiring): "Default limit = 1 for new
    /// products/groups" — admins get a sane baseline that they can
    /// override per-product-per-group from the Manage Products UI.
    ///
    /// This is a DOMAIN constant (not a config value) because the rule
    /// is universal — every new limit starts at 1. If the rule ever
    /// becomes configurable (e.g. per-tenant), promote this to a
    /// setting on SystemSettings.
    /// </summary>
    public const int DefaultLimit = 1;



    // ==================================================================================================================================
    //                                                          PROPERTIES
    // ==================================================================================================================================



    /// <summary>
    /// The Id of the <c>CustomerGroup</c> this limit applies to.
    /// Must match a <c>CustomerGroup.Id</c> exactly. EF Core enforces
    /// this via a foreign-key constraint at the database level.
    /// </summary>
    public Guid GroupId { get; }

    /// <summary>
    /// The maximum number of units a user in this group may purchase
    /// of the Product that owns this limit, per cart line item.
    /// Must be at least 1.
    /// </summary>
    public int Limit { get; }



    // ==================================================================================================================================
    //                                                          CONSTRUCTORS
    // ==================================================================================================================================



#pragma warning disable CS8618
    /// <summary>
    /// Parameterless constructor required by EF Core (for owned entity materialization).
    /// DO NOT use in application code — use <see cref="Create"/> instead.
    /// </summary>
    private CustomerGroupPurchaseLimit() { }
#pragma warning restore CS8618

    /// <summary>
    /// Private constructor used by the static factory method.
    /// </summary>
    private CustomerGroupPurchaseLimit(Guid groupId, int limit)
    {
        EnsureGroupIdValid(groupId);
        EnsureLimitValid(limit);

        GroupId = groupId;
        Limit = limit;
    }



    // ==================================================================================================================================
    //                                                          FACTORY METHOD
    // ==================================================================================================================================



    /// <summary>
    /// Creates a new CustomerGroupPurchaseLimit. This is the ONLY way to construct
    /// an instance from application code.
    /// </summary>
    public static CustomerGroupPurchaseLimit Create(Guid groupId, int limit)
    {
        return new CustomerGroupPurchaseLimit(groupId, limit);
    }



    // ==================================================================================================================================
    //                                                          CENTRALIZED GUARD METHODS
    // ==================================================================================================================================



    private static void EnsureGroupIdValid(Guid groupId)
    {
        if (groupId == Guid.Empty)
            throw new DomainException("Group Id is required for a purchase limit.");
    }

    private static void EnsureLimitValid(int limit)
    {
        if (limit < 1)
            throw new DomainException("Purchase limit must be at least 1.");
    }



    // ==================================================================================================================================
    //                                                          VALUE OBJECT INFRASTRUCTURE
    // ==================================================================================================================================



    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return GroupId;
        yield return Limit;
    }

    public override string ToString() => $"Group {GroupId}: {Limit}";
}