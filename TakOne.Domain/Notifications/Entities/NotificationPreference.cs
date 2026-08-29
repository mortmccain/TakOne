using TakOne.Domain.Notifications.Enums;
using TakOne.SharedKernel.Primitives;
using TakOne.SharedKernel.Common;

namespace TakOne.Domain.Notifications.Entities;

/// <summary>
/// Aggregate root for a single user's mute preference for ONE
/// <see cref="NotificationKind"/>. A user has AT MOST ONE row per kind
/// (unique index on <c>(UserId, Kind)</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>SPARSE STORAGE SEMANTICS</b>: the ABSENCE of a row means
/// "not muted" (the default). Rows are only created the first time a user
/// mutes a kind. Un-muting does NOT delete the row — it flips
/// <see cref="IsMuted"/> to <c>false</c> — so the user's explicit choice
/// is durable and re-toggling is a cheap UPDATE, never an INSERT/DELETE
/// cycle. This also keeps the preference visible in the settings UI
/// without a phantom-row dance.
/// </para>
/// <para>
/// <b>SUPPRESSION POINT (where mutes are enforced)</b>: the notification
/// CREATION path, not the read path. The Wolverine event handlers that
/// materialize <see cref="Notification"/> rows
/// (<c>NotifyOnSaleSubmittedEventHandler</c> etc.) consult
/// <c>INotificationPreferenceRepository.IsMutedAsync</c> before creating a
/// row. A muted kind therefore:
/// <list type="bullet">
///   <item>never INSERTs a Notification row (no unbounded unread-row
///         accumulation for muted kinds),</item>
///   <item>never raises <c>NotificationCreatedDomainEvent</c> (no SignalR
///         ping — the user's bell never flickers),</item>
///   <item>keeps the sale-audit trail in the Sales pages completely
///         unaffected (mutes only silence the notification bell, never
///         the business records).</item>
/// </list>
/// Muting is NOT retroactive: rows created before the mute stay in the
/// feed (and can be marked read / dismissed as usual).
/// </para>
/// <para>
/// <b>ALL KINDS ARE MUTABLE</b> — including <see cref="NotificationKind.Broadcast"/>
/// and <see cref="NotificationKind.AppUpdate"/>. Enterprise messenger
/// semantics: the user owns their attention. The admin's broadcast audit
/// list (<c>BroadcastNotifications</c>) still records every send with its
/// resolved recipient count, so muting never destroys the admin's
/// auditability — it only stops the per-user fanout row.
/// </para>
/// <para>
/// <b>NO DB-LEVEL FK TO USERS</b>: matches the codebase convention (see
/// <c>NotificationConfiguration</c>) — cross-aggregate references are bare
/// Guids. The Application layer resolves the user; the DB stores only the
/// snapshot.
/// </para>
/// <para>
/// <b>NO DOMAIN EVENTS</b>: a preference flip is a single-user settings
/// mutation with no downstream fanout — nothing to broadcast, and the
/// settings UI already reflects the change optimistically.
/// </para>
/// </remarks>
public sealed class NotificationPreference : AggregateRoot
{
    // ── PROPERTIES ──────────────────────────────────────────────────────

    /// <summary>
    /// The user this preference belongs to. Indexed (composite unique with
    /// <see cref="Kind"/>).
    /// </summary>
    public Guid UserId { get; private set; }

    /// <summary>
    /// The notification kind this preference mutes. Stored as int (enum
    /// conversion in EF Core) for index efficiency.
    /// </summary>
    public NotificationKind Kind { get; private set; }

    /// <summary>
    /// True = suppress notifications of <see cref="Kind"/> for
    /// <see cref="UserId"/> at creation time. False = normal delivery.
    /// See the class remarks for the sparse-storage semantics.
    /// </summary>
    public bool IsMuted { get; private set; }

    /// <summary>
    /// UTC timestamp of the last toggle. Diagnostic only (the settings UI
    /// does not render it); useful when investigating "why did user X stop
    /// getting notifications" — the answer is here, with a time.
    /// </summary>
    public DateTime UpdatedAtUtc { get; private set; }

    // ── CONSTRUCTORS ────────────────────────────────────────────────────

#pragma warning disable CS8618
    /// <summary>
    /// Parameterless ctor for EF Core. DO NOT use from application code.
    /// </summary>
    private NotificationPreference() : base(Guid.Empty) { }
#pragma warning restore CS8618

    private NotificationPreference(
        Guid userId,
        NotificationKind kind,
        bool isMuted)
        : base(Guid.NewGuid())
    {
        EnsureUserIdValid(userId);
        EnsureKindValid(kind);

        UserId = userId;
        Kind = kind;
        IsMuted = isMuted;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    // ── FACTORY ─────────────────────────────────────────────────────────

    /// <summary>
    /// The ONLY way to construct a <see cref="NotificationPreference"/>
    /// from application code. Creates the row in its initial mute state.
    /// </summary>
    /// <remarks>
    /// Prefer the Application-layer upsert flow
    /// (<c>SetNotificationMutedCommandHandler</c>) which decides between
    /// "create muted" and "toggle existing row" — this factory exists for
    /// the create branch.
    /// </remarks>
    public static NotificationPreference Create(
        Guid userId,
        NotificationKind kind,
        bool isMuted)
    {
        return new NotificationPreference(userId, kind, isMuted);
    }

    // ── BEHAVIOR ────────────────────────────────────────────────────────

    /// <summary>
    /// Mutes the kind for this user. Idempotent — muting an already-muted
    /// preference is a no-op (no spurious UPDATE, no timestamp churn that
    /// would confuse the "when did this change" diagnostic).
    /// </summary>
    public void Mute()
    {
        if (!IsMuted)
        {
            IsMuted = true;
            UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Un-mutes the kind for this user. Idempotent (same rationale as
    /// <see cref="Mute"/>). The row is intentionally NOT deleted — see the
    /// class-level sparse-storage remarks.
    /// </summary>
    public void Unmute()
    {
        if (IsMuted)
        {
            IsMuted = false;
            UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    // ── GUARDS ──────────────────────────────────────────────────────────

    private static void EnsureUserIdValid(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new DomainException(
                "A notification preference must belong to a non-empty user Id.");
        }
    }

    private static void EnsureKindValid(NotificationKind kind)
    {
        // Same defense-in-depth as Notification's ctor guard: an undefined
        // Kind would silently persist and break the (UserId, Kind) unique
        // index semantics + the settings UI's kind list rendering.
        if (!Enum.IsDefined(kind))
        {
            throw new DomainException(
                $"'{kind}' is not a valid {nameof(NotificationKind)} value.");
        }
    }
}
