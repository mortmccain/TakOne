namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Provides information about the currently authenticated user, sourced from
/// the HTTP context (or other ambient context) by the Infrastructure layer.
/// </summary>
public interface ICurrentUserService
{
    /// <summary>
    /// The Id of the authenticated user. Guid.Empty if not authenticated.
    /// </summary>
    Guid UserId { get; }

    /// <summary>
    /// The worker ID (login identifier) of the authenticated user.
    /// Empty string if not authenticated.
    /// </summary>
    string WorkerId { get; }

    /// <summary>
    /// The full name of the authenticated user, for display/audit.
    /// Empty string if not authenticated.
    /// </summary>
    string FullName { get; }

    /// <summary>
    /// The customer group Id of the authenticated user, or null if the
    /// user has no group (staff users). Sourced from the "GroupId" claim
    /// set at login time.
    ///
    /// NOTE (Salary feature, Step 3): This claim may be STALE — if an
    /// admin reassigns the user's group AFTER they logged in, the claim
    /// still reflects the OLD group. Handlers that need the CURRENT
    /// group (e.g. for purchase-limit or salary-budget checks) MUST
    /// re-load the user from the repository via
    /// <see cref="IUserRepository.GetByIdAsync"/>. The claim is only
    /// for fast-path UI gating (e.g. hiding the cart-budget bar for
    /// staff users) — never for authoritative budget enforcement.
    /// </summary>
    Guid? GroupId { get; }

    /// <summary>
    /// The gender of the authenticated user ("Male" or "Female"), or null if
    /// not authenticated or the claim is missing. Sourced from the "Gender"
    /// claim set at login time by Login.razor (which reads the Domain User's
    /// Gender). Used by the dashboard greeting ("Good morning, Mr./Ms. Smith").
    /// </summary>
    string? Gender { get; }

    /// <summary>
    /// Whether a user is currently authenticated.
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// Whether the authenticated user is in the given ASP.NET Identity role.
    /// Returns false if not authenticated.
    /// </summary>
    bool IsInRole(string role);
}