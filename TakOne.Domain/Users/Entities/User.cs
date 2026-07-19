using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.Primitives;

namespace TakOne.Domain.Users;

/// <summary>
/// Aggregate root for users. PURE DDD — no dependency on ASP.NET Identity,
/// EF Core, or any framework. Inherits only from <see cref="AggregateRoot"/>
/// (which inherits from <see cref="BaseEntity"/>, both in SharedKernel).
///
/// USAGE CONVENTIONS:
///   - <see cref="WorkerId"/> holds the user's PERSONAL WORKER ID
///     (e.g. "EMP-12345"), NOT their name. Customers, employees and managers
///     are all employees of some company; they authenticate with their worker ID.
///   - <see cref="FullName"/> holds their human-readable name.
///   - <see cref="GroupName"/> is set ONLY for customers, and is used to look up
///     per-product purchase limits. Customers must NEVER see this value in the UI;
///     it is an internal grouping mechanism.
///
/// IDENTITY MAPPING (done in Infrastructure layer, step 7):
///   The Domain User is mapped to an ApplicationUser : IdentityUser&lt;Guid&gt;
///   in Infrastructure. The mapping is:
///     - Domain User.Id            ↔ ApplicationUser.Id         (shared PK)
///     - Domain User.WorkerId      ↔ ApplicationUser.UserName   (login identifier)
///     - Domain User.IsActive      ↔ ApplicationUser.LockoutEnabled / custom flag
///   Email, PasswordHash, SecurityStamp, etc. live ONLY on ApplicationUser.
///   The Domain User knows nothing about email, password, or roles.
///
/// ROLES:
///   Roles are NOT modeled on this aggregate. They live in ASP.NET Identity's
///   IdentityRole&lt;Guid&gt; + AspNetUserRoles table, queried via UserManager.
///   The Domain has no concept of "Customer role" or "Manager role" — it only
///   knows about domain-level facts (WorkerId, FullName, GroupName, IsActive).
///
/// CREATION AUTHORIZATION:
///   Users can only be created by IT admins (Admin role) or managers.
///   This is enforced in the Application layer (authorization policy on the
///   command handler) and the WebUI (role-based page access). The Domain
///   does not enforce WHO can create users — only that the User itself is valid.
/// </summary>
public sealed class User : AggregateRoot
{



    // ==================================================================================================================================
    //                                                          PROPERTIES
    // ==================================================================================================================================



    /// <summary>
    /// The user's personal worker ID (e.g. "EMP-12345"). Used for login.
    /// NOTE: This is a DOMAIN property. In Infrastructure, this value is mapped
    /// to <see cref="Microsoft.AspNetCore.Identity.IdentityUser{TKey}.UserName"/>
    /// on the ApplicationUser.
    /// </summary>
    public string WorkerId { get; private set; }

    /// <summary>
    /// The user's full name (e.g. "John Smith"). Used for display and auditing only,
    /// never for login.
    /// </summary>
    public string FullName { get; private set; }

    /// <summary>
    /// The customer group this user belongs to. NULL for non-customer users
    /// (employees, managers, read-only, admin). Used to look up per-product
    /// purchase limits. Customers must NEVER see this value in the UI.
    /// </summary>
    public string? GroupName { get; private set; }

    /// <summary>
    /// Whether the user can log in and perform actions.
    /// Inactive users remain in the database for audit purposes but cannot authenticate.
    /// Soft-delete is preferred over hard-delete.
    /// </summary>
    public bool IsActive { get; private set; }



    // ==================================================================================================================================
    //                                                          CONSTRUCTORS
    // ==================================================================================================================================



#pragma warning disable CS8618
    /// <summary>
    /// Parameterless constructor required by EF Core. DO NOT use in application code.
    /// </summary>
    private User() : base(Guid.Empty) { }
#pragma warning restore CS8618

    /// <summary>
    /// Private constructor used by the static factory methods.
    /// </summary>
    private User(string workerId, string fullName, string? groupName) : base(Guid.NewGuid())
    {
        EnsureWorkerIdValid(workerId);
        EnsureFullNameValid(fullName);
        // groupName can be null for staff users; only validated if non-null (see CreateCustomer).

        WorkerId = workerId;
        FullName = fullName;
        GroupName = groupName;
        IsActive = true;
    }



    // ==================================================================================================================================
    //                                                          FACTORY METHODS
    // ==================================================================================================================================



    /// <summary>
    /// Creates a new CUSTOMER user. Customers MUST belong to a group so that
    /// per-product purchase limits can be applied to them at sale time.
    ///
    /// The "Customer" ASP.NET Identity role is assigned separately by the
    /// application layer (UserManager.AddToRoleAsync) AFTER this method returns
    /// and AFTER the ApplicationUser has been created in Infrastructure.
    /// </summary>
    public static User CreateCustomer(string workerId, string fullName, string groupName)
    {
        EnsureGroupNameValid(groupName);
        return new User(workerId, fullName, groupName);
    }

    /// <summary>
    /// Creates a new STAFF user (employee, manager, read-only, or admin).
    /// Staff users do NOT belong to a customer group, so GroupName is null.
    /// The specific role is assigned separately by the application layer
    /// via UserManager.AddToRoleAsync.
    /// </summary>
    public static User CreateStaff(string workerId, string fullName)
    {
        return new User(workerId, fullName, groupName: null);
    }



    // ==================================================================================================================================
    //                                                          BEHAVIOR (domain methods)
    // ==================================================================================================================================



    /// <summary>
    /// Assigns the user to a customer group. Only meaningful for users who are
    /// (or will be) in the Customer role.
    /// </summary>
    public void AssignToGroup(string groupName)
    {
        EnsureGroupNameValid(groupName);
        GroupName = groupName;
    }

    /// <summary>
    /// Removes the user from their customer group. After this, per-product
    /// purchase limits will not apply to them — typically you'd only call this
    /// when converting a customer to a staff role or deactivating them.
    /// </summary>
    public void RemoveFromGroup()
    {
        GroupName = null;
    }

    /// <summary>
    /// Updates the user's full name.
    /// </summary>
    public void ChangeFullName(string newFullName)
    {
        EnsureFullNameValid(newFullName);
        FullName = newFullName;
    }

    /// <summary>
    /// Deactivates the user. They cannot log in while inactive.
    /// This is the soft-delete path; we keep the row for audit/history.
    /// </summary>
    public void Deactivate() => IsActive = false;

    /// <summary>
    /// Reactivates a previously deactivated user.
    /// </summary>
    public void Activate() => IsActive = true;



    // ==================================================================================================================================
    //                                                          CENTRALIZED GUARD METHODS
    // ==================================================================================================================================



    // These are DOMAIN-LEVEL invariants. They enforce the absolute minimum needed
    // for the User to be valid at all times — the Domain NEVER trusts the caller.
    //
    // Stricter validation (exact length bounds, regex format, uniqueness, etc.)
    // is done in the Application layer via FluentValidation, which gives the
    // user friendly error messages BEFORE the command reaches the domain.
    //
    // The bounds here are deliberately generous upper limits — they exist to
    // protect the database column, not to enforce a specific format.

    private static void EnsureWorkerIdValid(string workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId))
            throw new DomainException("Worker ID is required.");

        if (workerId.Length > 100)
            throw new DomainException("Worker ID cannot exceed 100 characters.");
    }

    private static void EnsureFullNameValid(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            throw new DomainException("Full name is required.");

        if (fullName.Length > 200)
            throw new DomainException("Full name cannot exceed 200 characters.");
    }

    private static void EnsureGroupNameValid(string groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            throw new DomainException("Group name is required for customers.");

        if (groupName.Length > 100)
            throw new DomainException("Group name cannot exceed 100 characters.");
    }
}