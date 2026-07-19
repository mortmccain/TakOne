using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.Primitives;

namespace TakOne.Domain.Products.ValueObjects;

/// <summary>
/// Value object that defines how many units of a specific Product
/// a user in a given customer group is allowed to buy.
///
/// Stored as part of the Product aggregate (Product owns a collection of these).
/// Equality is by (GroupName, Limit) — there is no Id, because this is a value object.
///
/// IMMUTABILITY:
///   Value objects are immutable. To "change" a limit, the Product aggregate
///   replaces the old instance with a new one (see Product.SetPurchaseLimit).
///
/// EF CORE MAPPING (Infrastructure, step 7):
///   Mapped as an owned collection (OwnsMany) on the Product table.
///   EF will create a shadow primary key in the database; the Domain class
///   has no Id property.
/// </summary>
public sealed class CustomerGroupPurchaseLimit : BaseValueObject
{



    // ==================================================================================================================================
    //                                                          PROPERTIES
    // ==================================================================================================================================



    /// <summary>
    /// The name of the customer group this limit applies to.
    /// Must match a User.GroupName exactly (case-sensitive).
    /// </summary>
    public string GroupName { get; }

    /// <summary>
    /// The maximum number of units a user in this group may purchase
    /// of the Product that owns this limit. Must be at least 1.
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
    private CustomerGroupPurchaseLimit(string groupName, int limit)
    {
        EnsureGroupNameValid(groupName);
        EnsureLimitValid(limit);

        GroupName = groupName;
        Limit = limit;
    }



    // ==================================================================================================================================
    //                                                          FACTORY METHOD
    // ==================================================================================================================================



    /// <summary>
    /// Creates a new CustomerGroupPurchaseLimit. This is the ONLY way to construct
    /// an instance from application code.
    /// </summary>
    public static CustomerGroupPurchaseLimit Create(string groupName, int limit)
    {
        return new CustomerGroupPurchaseLimit(groupName, limit);
    }



    // ==================================================================================================================================
    //                                                          CENTRALIZED GUARD METHODS
    // ==================================================================================================================================



    private static void EnsureGroupNameValid(string groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            throw new DomainException("Group name is required for a purchase limit.");

        if (groupName.Length > 100)
            throw new DomainException("Group name cannot exceed 100 characters.");
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
        yield return GroupName;
        yield return Limit;
    }

    public override string ToString() => $"{GroupName}: {Limit}";
}