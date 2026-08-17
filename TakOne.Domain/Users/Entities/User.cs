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
///   - <see cref="GroupId"/> is set ONLY for customers, and is used to look up
///     per-product purchase limits and the monthly salary budget. Customers
///     must NEVER see this value or the word "group" in the UI; it is an
///     internal grouping mechanism.
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
///   knows about domain-level facts (WorkerId, FullName, GroupId, IsActive).
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
    /// The Id of the <c>CustomerGroup</c> this user belongs to. NULL for
    /// non-customer users (employees, managers, read-only, admin). Used to
    /// look up per-product purchase limits and the monthly salary budget.
    ///
    /// Customers must NEVER see this value or the word "group" in the UI —
    /// all customer-facing errors are generic with an internal error code.
    ///
    /// This is a Guid FK (not a string group name) because groups are now
    /// first-class aggregates (see <c>CustomerGroup</c>). Renaming a group
    /// no longer requires touching every User row — the FK stays the same.
    /// </summary>
    public Guid? GroupId { get; private set; }

    /// <summary>
    /// Whether the user can log in and perform actions.
    /// Inactive users remain in the database for audit purposes but cannot authenticate.
    /// Soft-delete is preferred over hard-delete.
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// The user's gender. Per roadmap Section 12.5 (locked-in): 2-value enum
    /// (Male=0, Female=1), default Male. Stored as int column on the Users
    /// table. Used by the user-management admin page (display only — never
    /// affects authorization or business rules).
    ///
    /// Set at creation time via the factory methods' <c>gender</c> parameter.
    /// Mutable via <see cref="ChangeGender"/> (called by an admin from the
    /// user-management page if a user's gender was recorded incorrectly).
    /// </summary>
    public Gender Gender { get; private set; }



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
    private User(string workerId, string fullName, Guid? groupId, Gender gender) : base(Guid.NewGuid())
    {
        EnsureWorkerIdValid(workerId);
        EnsureFullNameValid(fullName);
        // groupId can be null for staff users; only validated if non-null (see CreateCustomer).
        if (groupId is not null)
        {
            EnsureGroupIdValid(groupId.Value);
        }
        EnsureGenderValid(gender);

        WorkerId = workerId;
        FullName = fullName;
        GroupId = groupId;
        Gender = gender;
        IsActive = true;
    }



    // ==================================================================================================================================
    //                                                          FACTORY METHODS
    // ==================================================================================================================================



    /// <summary>
    /// Creates a new CUSTOMER user. Customers MUST belong to a group so that
    /// per-product purchase limits and the monthly salary budget can be
    /// applied to them at sale time.
    ///
    /// The "Customer" ASP.NET Identity role is assigned separately by the
    /// application layer (UserManager.AddToRoleAsync) AFTER this method returns
    /// and AFTER the ApplicationUser has been created in Infrastructure.
    /// </summary>
    /// <param name="groupId">The Id of the CustomerGroup this customer belongs to.</param>
    /// <param name="gender">
    /// The user's gender. Per roadmap Section 12.5, only Male/Female are
    /// supported. Defaults to Male if the caller doesn't care.
    /// </param>
    public static User CreateCustomer(string workerId, string fullName, Guid groupId, Gender gender = Gender.Male)
    {
        EnsureGroupIdValid(groupId);
        return new User(workerId, fullName, groupId, gender);
    }

    /// <summary>
    /// Creates a new STAFF user (employee, manager, read-only, or admin).
    /// Staff users do NOT belong to a customer group, so GroupId is null.
    /// The specific role is assigned separately by the application layer
    /// via UserManager.AddToRoleAsync.
    /// </summary>
    /// <param name="gender">
    /// The user's gender. Per roadmap Section 12.5, only Male/Female are
    /// supported. Defaults to Male if the caller doesn't care.
    /// </param>
    public static User CreateStaff(string workerId, string fullName, Gender gender = Gender.Male)
    {
        return new User(workerId, fullName, groupId: null, gender);
    }



    // ==================================================================================================================================
    //                                                          BEHAVIOR (domain methods)
    // ==================================================================================================================================



    /// <summary>
    /// Assigns the user to a customer group. Only meaningful for users who are
    /// (or will be) in the Customer role.
    /// </summary>
    public void AssignToGroup(Guid groupId)
    {
        EnsureGroupIdValid(groupId);
        GroupId = groupId;
    }

    /// <summary>
    /// Removes the user from their customer group. After this, per-product
    /// purchase limits and the salary budget will not apply to them —
    /// typically you'd only call this when converting a customer to a
    /// staff role or deactivating them.
    /// </summary>
    public void RemoveFromGroup()
    {
        GroupId = null;
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
    /// Updates the user's gender. Per roadmap Section 12.5, only Male and
    /// Female are valid values — any other value throws DomainException.
    /// This method is typically called by an admin correcting a data-entry
    /// mistake on the user-management page.
    /// </summary>
    public void ChangeGender(Gender newGender)
    {
        EnsureGenderValid(newGender);
        Gender = newGender;
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

    /// <summary>
    /// Validates that the supplied GroupId is not empty. A non-null but
    /// empty Guid indicates a programming error (caller forgot to set it
    /// or passed Guid.Empty by mistake).
    /// </summary>
    private static void EnsureGroupIdValid(Guid groupId)
    {
        if (groupId == Guid.Empty)
            throw new DomainException("Customer group Id is required for customers.");
    }

    /// <summary>
    /// Validates that the supplied Gender is a defined enum value.
    /// C# enums can hold ANY integer (e.g. <c>(Gender)42</c>), so we must
    /// explicitly check that the value is one of the two defined members.
    /// Per roadmap Section 12.5, only Male (0) and Female (1) are valid.
    /// </summary>
    private static void EnsureGenderValid(Gender gender)
    {
        if (!Enum.IsDefined(typeof(Gender), gender))
        {
            throw new DomainException(
                $"Gender must be one of: {string.Join(", ", Enum.GetNames(typeof(Gender)))}.");
        }
    }
}