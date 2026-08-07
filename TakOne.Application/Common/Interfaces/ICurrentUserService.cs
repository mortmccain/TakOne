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
    /// The customer group name of the authenticated user, or null if the user
    /// has no group (staff users). Used to look up per-product purchase limits.
    /// </summary>
    string? GroupName { get; }

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