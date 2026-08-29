using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Notifications.Errors;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Notifications.Commands.SetNotificationMuted;

/// <summary>
/// Handler for <see cref="SetNotificationMutedCommand"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>UPSERT FLOW</b>:
/// <list type="number">
///   <item>Load the (UserId, Kind) row — TRACKED (load-for-mutation).</item>
///   <item>Row exists → <c>Mute()</c> / <c>Unmute()</c> (idempotent domain
///         methods; no-op when the state already matches).</item>
///   <item>No row + mute → create it via <c>NotificationPreference.Create</c>
///         (sparse storage: rows exist only for explicitly-muted kinds).</item>
///   <item>No row + unmute → no write at all (already the default) —
///         still returns Success.</item>
/// </list>
/// Persisted by the explicit <c>SaveChangesAsync</c> inside Wolverine's
/// AutoApplyTransactions-enrolled EF Core transaction (same pattern as
/// <c>MarkNotificationAsReadCommandHandler</c>).
/// </para>
/// <para>
/// <b>RACE BEHAVIOUR</b>: two concurrent "mute kind X" commands from the
/// same user (double-click, two tabs) both see "no row" and both INSERT;
/// the second loses with a 2627 unique-index violation inside the
/// transaction and fails cleanly — Wolverine does NOT retry (the command
/// is not marked retryable), the UI toast shows the error, and the next
/// Settings-page load shows the truth (the kind IS muted — the winner's
/// row). Correct final state, no corruption; a retry-free UX failure is
/// acceptable for a settings toggle.
/// </para>
/// <para>
/// <b>STABLE ERROR CODES</b>: culture-neutral codes from
/// <see cref="NotificationErrors"/> for both the auth and the invalid-kind
/// paths so the UI can localize per CurrentUICulture.
/// </para>
/// </remarks>
public sealed class SetNotificationMutedCommandHandler
{
    public static async Task<Result> HandleAsync(
        SetNotificationMutedCommand command,
        ICurrentUserService currentUser,
        INotificationPreferenceRepository preferenceRepository,
        IUnitOfWork unitOfWork,
        ILogger<SetNotificationMutedCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        // ── 1. Defense-in-depth auth check (attribute should precede). ──
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning(
                "SetNotificationMuted: unauthenticated call rejected.");
            return Result.Failure(NotificationErrors.FormatAuthRequired());
        }

        // ── 2. Validate the kind. An undefined enum value ((NotificationKind)42
        //       from a bad bind) would otherwise surface as a 500 from the
        //       domain factory's DomainException — a stable code keeps the
        //       failure localizable + retryable-by-user. ──
        if (!Enum.IsDefined(command.Kind))
        {
            logger.LogWarning(
                "SetNotificationMuted: rejected undefined kind {Kind} for user {UserId}.",
                command.Kind, currentUser.UserId);
            return Result.Failure(NotificationErrors.FormatInvalidKind());
        }

        // ── 3. Upsert. ──
        var preference = await preferenceRepository.GetForUserAsync(
            currentUser.UserId, command.Kind, cancellationToken);

        if (preference is not null)
        {
            // Tracked entity — Mute/Unmute are idempotent no-ops when the
            // state already matches (no spurious UPDATE, no timestamp churn).
            if (command.IsMuted)
            {
                preference.Mute();
            }
            else
            {
                preference.Unmute();
            }
        }
        else if (command.IsMuted)
        {
            // Sparse storage: the FIRST mute creates the row. Unmuting a
            // never-muted kind stays row-less (it's already the default).
            preference = Domain.Notifications.Entities.NotificationPreference.Create(
                userId: currentUser.UserId,
                kind: command.Kind,
                isMuted: true);

            await preferenceRepository.AddAsync(preference, cancellationToken);
        }
        else
        {
            // No row + unmute: nothing to persist. Success without a write —
            // idempotent by construction.
            logger.LogDebug(
                "SetNotificationMuted: no-op unmute (no persisted row) for user {UserId}, kind {Kind}.",
                currentUser.UserId, command.Kind);
            return Result.Success();
        }

        // ── 4. Persist — Wolverine's transactional middleware enrolls this
        //       handler in an EF Core transaction; the explicit
        //       SaveChangesAsync inside it is what flushes the mutation /
        //       INSERT. See MarkNotificationAsReadCommandHandler's identical
        //       IUnitOfWork rationale. ──
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "SetNotificationMuted: user {UserId} set kind {Kind} muted={IsMuted}.",
            currentUser.UserId, command.Kind, command.IsMuted);

        return Result.Success();
    }
}
