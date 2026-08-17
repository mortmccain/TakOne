using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.Primitives;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Domain.Customers.Entities;

/// <summary>
/// Aggregate root for a customer group.
///
/// A customer group is a named bucket of customers who share the same
/// monthly salary budget and the same per-product purchase-limit table.
/// Each group has:
///   - A unique name (e.g. "Management", "Staff", "Contractors").
///   - A monthly salary (a <see cref="Money"/> value object: amount +
///     ISO currency code). The amount is the maximum a single user in
///     this group may spend per Persian calendar month.
///   - An active flag. Inactive groups cannot be assigned to new users,
///     and users in an inactive group cannot make new purchases (but
///     existing submitted sales are unaffected).
///
/// WHAT THIS AGGREGATE OWNS:
///   - Group identity (Id, Name).
///   - Group salary (Money).
///   - Group active status.
///
/// WHAT THIS AGGREGATE DOES NOT OWN:
///   - The per-product count limits. Those live on the Product aggregate
///     as <see cref="TakOne.Domain.Products.ValueObjects.CustomerGroupPurchaseLimit"/>
///     value objects, keyed by <c>GroupId</c>. The limits are Product-owned
///     because the common case is "new product, set limits for all groups"
///     — flipping the ownership would make the new-product flow awkward
///     (you'd have to iterate all groups to add a limit row for the new
///     product). The new-group flow is the awkward case under this design
///     (iterate all products to add default limits), but new groups are
///     rare (5-15 expected over the app's lifetime) while new products
///     are common, so the trade-off favours Product ownership.
///
/// CUSTOMER-FACING VISIBILITY:
///   Customers must NEVER see the word "group" in any UI message.
///   All customer-facing errors (e.g. "your monthly budget is X") are
///   generic and contain an internal error code for support. The
///   group concept is admin/manager-only vocabulary.
///
/// CURRENCY NOTE:
///   The salary currency also defines what currency of products the
///   group's users may buy. A user whose group salary is in IRR may
///   only buy IRR-priced products; a USD-salary group may only buy
///   USD-priced products. This currency match is enforced on EVERY
///   cart mutation, in EVERY limit mode (including CountOnly), by
///   <c>IPurchaseLimitPolicy</c>.
/// </summary>
public sealed class CustomerGroup : AggregateRoot
{



    // ==================================================================================================================================
    //                                                          PROPERTIES
    // ==================================================================================================================================



    /// <summary>
    /// The unique display name of the group (e.g. "Management").
    /// Must be 1-100 characters. Uniqueness is enforced at the database
    /// level by a unique index on this column.
    /// </summary>
    public string Name { get; private set; }

    /// <summary>
    /// The monthly salary budget for any user in this group. The amount
    /// is the maximum a single user may spend per Persian calendar month
    /// (sum of draft cart total + submitted non-cancelled sales this month).
    /// The currency defines what currency of products the group may buy.
    /// </summary>
    public Money Salary { get; private set; }

    /// <summary>
    /// Whether the group is active. Inactive groups cannot be assigned to
    /// new users, and users in an inactive group cannot make new purchases.
    /// Soft-delete is preferred over hard-delete so historical sales
    /// remain queryable.
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// The UTC timestamp when the group was created. For audit display.
    /// </summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>
    /// The UTC timestamp of the last update to any field on this row.
    /// For audit display. Set automatically by the domain methods
    /// (rename, update salary, activate/deactivate).
    /// </summary>
    public DateTime UpdatedAt { get; private set; }



    // ==================================================================================================================================
    //                                                          CONSTRUCTORS
    // ==================================================================================================================================



#pragma warning disable CS8618
    /// <summary>
    /// Parameterless constructor required by EF Core. DO NOT use in
    /// application code — use <see cref="Create"/> instead.
    /// </summary>
    private CustomerGroup() : base(Guid.Empty) { }
#pragma warning restore CS8618

    /// <summary>
    /// Private constructor used by the static factory method.
    /// </summary>
    private CustomerGroup(string name, Money salary) : base(Guid.NewGuid())
    {
        EnsureNameValid(name);
        EnsureSalaryValid(salary);

        Name = name;
        Salary = salary;
        IsActive = true;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = CreatedAt;
    }



    // ==================================================================================================================================
    //                                                          FACTORY METHOD
    // ==================================================================================================================================



    /// <summary>
    /// Creates a new active customer group. This is the ONLY way to
    /// construct a CustomerGroup from application code.
    ///
    /// The caller (CreateCustomerGroupCommandHandler) is responsible
    /// for also bulk-inserting default per-product purchase limits
    /// for the new group across all existing products. This aggregate
    /// does NOT own those limit rows — see the class-level comment.
    /// </summary>
    /// <param name="name">Unique group name, 1-100 chars.</param>
    /// <param name="salary">Monthly salary budget (Money = amount + currency).</param>
    public static CustomerGroup Create(string name, Money salary)
    {
        return new CustomerGroup(name, salary);
    }



    // ==================================================================================================================================
    //                                                          BEHAVIOR
    // ==================================================================================================================================



    /// <summary>
    /// Renames the group. The new name must still be unique at the
    /// database level — the domain cannot enforce uniqueness, but the
    /// Infrastructure repository will reject a duplicate via a unique
    /// index violation (translated to a friendly error by the application
    /// layer).
    /// </summary>
    public void Rename(string newName)
    {
        EnsureNameValid(newName);

        if (newName == Name) return;

        Name = newName;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Updates the group's monthly salary. A change here takes effect
    /// immediately for new purchase attempts — already-consumed budget
    /// (from submitted sales earlier in the month) is still counted
    /// against the new salary.
    ///
    /// If the admin lowers the salary below what a user has already
    /// consumed this month, that user cannot add anything more until
    /// the next Persian month reset. This is intentional — the salary
    /// is the current ceiling, not a starting allowance.
    /// </summary>
    public void UpdateSalary(Money newSalary)
    {
        EnsureSalaryValid(newSalary);

        if (newSalary == Salary) return;

        Salary = newSalary;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Deactivates the group. Users in this group cannot make new
    /// purchases until it's reactivated. Existing submitted sales are
    /// unaffected. Existing draft carts remain visible to the user
    /// but cannot be modified or submitted.
    /// </summary>
    public void Deactivate()
    {
        if (!IsActive) return;

        IsActive = false;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Reactivates a previously-deactivated group.
    /// </summary>
    public void Activate()
    {
        if (IsActive) return;

        IsActive = true;
        UpdatedAt = DateTime.UtcNow;
    }



    // ==================================================================================================================================
    //                                                          CENTRALIZED GUARD METHODS
    // ==================================================================================================================================



    // These are DOMAIN-LEVEL invariants. They enforce the absolute minimum
    // needed for the CustomerGroup to be valid at all times. Stricter
    // validation (exact length bounds, regex format, uniqueness) is done
    // in the Application layer via FluentValidation.

    private static void EnsureNameValid(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Customer group name is required.");

        if (name.Length > 100)
            throw new DomainException("Customer group name cannot exceed 100 characters.");
    }

    private static void EnsureSalaryValid(Money salary)
    {
        // Money itself enforces non-null currency and 3-letter ISO code;
        // here we only add the domain rule that salary cannot be negative.
        // Zero is allowed (a zero-salary group can be created but no
        // purchases are allowed in SalaryOnly or Both mode — count-only
        // mode still works). This is a valid "blocked" state.
        if (salary.Amount < 0)
            throw new DomainException("Customer group salary cannot be negative.");
    }
}