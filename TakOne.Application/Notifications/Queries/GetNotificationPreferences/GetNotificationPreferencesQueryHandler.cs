using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Notifications.DTOs;
using TakOne.Application.Notifications.Errors;
using TakOne.Domain.Notifications.Enums;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Notifications.Queries.GetNotificationPreferences;

/// <summary>
/// Handler for <see cref="GetNotificationPreferencesQuery"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>COMPLETE LIST</b>: the result always contains one
/// <see cref="NotificationPreferenceDto"/> per enum value, ordered by the
/// enum's numeric value (stable render order). Kinds with no persisted
/// row default to <c>IsMuted = false</c> — see the sparse-storage
/// semantics on the <c>NotificationPreference</c> aggregate.
/// </para>
/// <para>
/// <b>COST</b>: one indexed read of the user's (at most 7) preference
/// rows + an in-memory merge against <see cref="Enum.GetValues"/> —
/// negligible; the Settings page is cold-path UI.
/// </para>
/// <para>
/// <b>STABLE ERROR CODE</b>: unauthenticated callers get
/// <see cref="NotificationErrors.FormatAuthRequired"/> (culture-neutral,
/// UI-localizable) — the same convention as the other notification
/// handlers. (Defense-in-depth: the [RequireAuthentication] attribute
/// should already have rejected the call at the Wolverine boundary.)
/// </para>
/// </remarks>
public sealed class GetNotificationPreferencesQueryHandler
{
    public static async Task<Result<IReadOnlyList<NotificationPreferenceDto>>> HandleAsync(
        GetNotificationPreferencesQuery query,
        ICurrentUserService currentUser,
        INotificationPreferenceRepository preferenceRepository,
        ILogger<GetNotificationPreferencesQueryHandler> logger,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning(
                "GetNotificationPreferences: unauthenticated call rejected.");
            return Result<IReadOnlyList<NotificationPreferenceDto>>.Failure(
                NotificationErrors.FormatAuthRequired());
        }

        // Load the user's sparse preference rows (typically zero — most
        // users never mute anything).
        var persisted = await preferenceRepository.GetAllForUserAsync(
            currentUser.UserId, cancellationToken);

        // Index by kind for the merge below. Duplicate kinds are impossible
        // (unique index UX_NotificationPreferences_UserId_Kind); ToDictionary
        // would throw if the invariant were ever violated, which is exactly
        // the loud failure we want (silently dropping a row would hide a
        // data-integrity bug).
        var byKind = persisted.ToDictionary(p => p.Kind, p => p.IsMuted);

        // One DTO per enum value, enum-ordered. New kinds appended to the
        // enum appear here automatically on the next Settings-page load —
        // no code change needed in this handler or the UI list rendering.
        var dtos = Enum.GetValues<NotificationKind>()
            .Select(kind => new NotificationPreferenceDto
            {
                Kind = kind,
                IsMuted = byKind.TryGetValue(kind, out var muted) && muted
            })
            .ToList();

        return Result<IReadOnlyList<NotificationPreferenceDto>>.Success(dtos);
    }
}
