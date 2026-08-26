using TakOne.Application.Common.Authorization;
using TakOne.Domain.Notifications.Enums;

namespace TakOne.Application.Notifications.Commands.SendBroadcastNotification;

/// <summary>
/// Admin-authored broadcast notification. Resolves the recipient audience
/// from <see cref="Scope"/> + the target fields, creates ONE
/// <see cref="Domain.Notifications.Entities.BroadcastNotification"/> audit
/// row + N per-user <see cref="Domain.Notifications.Entities.Notification"/>
/// fanout rows (one per recipient) in the same EF Core transaction, and
/// raises <c>NotificationCreatedDomainEvent</c> per fanout row so the
/// existing <c>NotificationCreatedBroadcastHandler</c> pings SignalR for
/// each recipient's UI in real time.
/// </summary>
/// <remarks>
/// <para>
/// <b>AUTHORIZATION</b>: <c>[RequireRoles(Admin)]</c> — only Admin-role
/// users may send broadcasts. Managers, Employees, and Customers cannot
/// (a manager announcing a company-wide policy change is a workflow that
/// should go through an admin). This is enforced both by Wolverine's
/// <c>AuthorizationPolicyVerifier</c> middleware (rejects the command
/// before the handler runs) and by the handler's defense-in-depth auth
/// check.
/// </para>
/// <para>
/// <b>SCOPE-TARGET CONSISTENCY</b>: exactly one of
/// <see cref="TargetRoleName"/>/<see cref="TargetGroupId"/>/<see cref="TargetUserId"/>
/// must be set according to <see cref="Scope"/>. The validator enforces
/// this; the <see cref="Domain.Notifications.Entities.BroadcastNotification.Create"/>
/// factory re-enforces it (throws DomainException if violated).
/// </para>
/// <para>
/// <b>TRANSACTIONAL INVARIANT</b>: the audit row + all N fanout rows
/// persist atomically. If SaveChangesAsync fails, NO fanout row reaches
/// any user, NO SignalR ping is sent, NO audit row is left dangling.
/// The Wolverine outbox also guarantees the per-user
/// <c>NotificationCreatedDomainEvent</c> messages are only delivered to
/// the broadcast handler AFTER the originating transaction commits —
/// so a rollback means no false SignalR ping either.
/// </para>
/// <para>
/// <b>RETURN VALUE</b>: <c>Result&lt;int&gt;</c> — the recipient count.
/// The UI shows "Broadcast sent to N users" using this.
/// </para>
/// </remarks>
[RequireRoles(Roles.Admin)]
public sealed record SendBroadcastNotificationCommand(
    string Title,
    string Message,
    BroadcastScope Scope,
    string? TargetRoleName,
    Guid? TargetGroupId,
    Guid? TargetUserId);
