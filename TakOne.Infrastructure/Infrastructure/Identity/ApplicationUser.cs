using Microsoft.AspNetCore.Identity;

namespace TakOne.Infrastructure.Identity;

/// <summary>
/// The ASP.NET Identity user entity. Stored in the <c>AspNetUsers</c> table
/// (auto-created by Identity's EF Core migrations).
///
/// RELATIONSHIP TO THE DOMAIN <c>User</c> AGGREGATE:
///   The Domain layer has its own <c>TakOne.Domain.Users.User</c> aggregate
///   (pure POCO, no framework dependencies). This <c>ApplicationUser</c> is
///   its INFRASTRUCTURE-side counterpart for everything Identity-related:
///   password hashing, email confirmation, two-factor auth, lockout, roles,
///   security stamps.
///
///   The two share the SAME primary key (a <see cref="Guid"/>). When a new
///   user is created:
///     1. Application layer creates the Domain User via
///        <c>User.CreateCustomer(...)</c> / <c>User.CreateStaff(...)</c>
///        — gets a new Guid.
///     2. Application layer calls <c>IUserRepository.AddAsync(domainUser)</c>
///        — EF tracks it for SaveChanges.
///     3. Application layer calls
///        <c>IUserAccountService.CreateIdentityAccountAsync(domainUser.Id,
///        workerId, email, password, role)</c> — Infrastructure creates this
///        <c>ApplicationUser</c> with the SAME Guid, sets its password, and
///        assigns the role.
///     4. <c>IUnitOfWork.SaveChangesAsync</c> commits BOTH rows in one
///        transaction (because they share the same DbContext).
///
/// PROPERTY MAPPING (Domain User ↔ ApplicationUser):
///   Domain User.Id          ↔ ApplicationUser.Id         (shared PK)
///   Domain User.WorkerId    ↔ ApplicationUser.UserName   (login identifier)
///   Domain User.FullName    ↔ (not on ApplicationUser — read from Domain
///                              User at login time and stuffed into claims)
///   Domain User.GroupName   ↔ (not on ApplicationUser — same as FullName)
///   Domain User.IsActive    ↔ ApplicationUser.IsActive   (custom flag, see below)
///
/// WHY WE KEEP FULLNAME / GROUPNAME OFF ApplicationUser:
///   They're domain facts, not Identity facts. Identity doesn't need them
///   to authenticate. The Application layer's <c>ICurrentUserService</c>
///   (implemented in the WebUI layer) reads them from claims that are set
///   at login time, populated by querying the Domain User. This avoids
///   duplicating data across two tables and keeps the Domain authoritative
///   for those facts.
///
/// WHY WE ADD IsActive HERE (when the Domain User already has it):
///   ASP.NET Identity's <c>SignInManager</c> doesn't consult our Domain
///   User when deciding whether to issue a cookie — it consults the
///   <c>ApplicationUser</c> and the standard <c>LockoutEnabled</c> /
///   <c>LockoutEnd</c> fields. We COULD overload <c>LockoutEnd</c> to mean
///   "admin-deactivated" by setting it to <c>DateTimeOffset.MaxValue</c>,
///   but that conflates two different concepts:
///     - Admin soft-deletes the user (our domain concept).
///     - Identity auto-locks the user after N failed attempts (framework concept).
///   Keeping them separate makes audit logs clearer and lets admins
///   re-activate a user without clearing lockout state.
///   The <c>IUserAccountService</c> implementation is responsible for
///   keeping the two <c>IsActive</c> flags in sync (Domain + Identity).
/// </summary>
public class ApplicationUser : IdentityUser<Guid>
{
    /// <summary>
    /// Whether the user is allowed to log in. Set to <c>false</c> when an
    /// admin deactivates the user (mirrors
    /// <c>TakOne.Domain.Users.User.IsActive</c> = false).
    ///
    /// Defaults to <c>true</c> because new users are active until deactivated.
    ///
    /// The <c>SignInManager</c> sign-in flow checks this via a custom
    /// <c>IUserValidator</c> / lockout check in the WebUI layer — Identity
    /// doesn't natively consult custom flags, but a small extension hook
    /// (configured in <c>AddTakOneInfrastructure</c>) makes a deactivated
    /// user fail sign-in with a clear error.
    /// </summary>
    public bool IsActive { get; set; } = true;
}