namespace TakOne.Application.Users.Commands.ImportUsers;

/// <summary>
/// What the user-import handler did with one CSV row.
/// </summary>
public enum UserImportRowOutcome
{
    /// <summary>
    /// The worker ID was unknown — a new user was created (Domain User +
    /// Identity account + roles [+ group]), with the default password
    /// formula and the forced one-time password change.
    /// </summary>
    Created = 0,

    /// <summary>
    /// A user with the worker ID already exists — the row was SKIPPED
    /// untouched. Re-importing an export is therefore idempotent and can
    /// never silently mutate live accounts.
    /// </summary>
    Skipped = 1,

    /// <summary>
    /// The row was rejected (empty worker ID / name, unknown role,
    /// unknown group, Identity rejection, …). Nothing was written for
    /// this row; every other row was still processed.
    /// </summary>
    Failed = 2
}

/// <summary>
/// The outcome of one row of a user-import CSV. Mirrors the product
/// import's per-row report so both Settings cards behave alike.
/// </summary>
public sealed record UserImportRowResultDto
{
    /// <summary>
    /// 1-based data-row number in the source CSV. Echoed from
    /// <c>UserImportRowDto.RowNumber</c>.
    /// </summary>
    public int RowNumber { get; init; }

    /// <summary>
    /// The worker ID the row carried (empty when the cell was empty — the
    /// failure message explains what's missing).
    /// </summary>
    public string WorkerId { get; init; } = string.Empty;

    /// <summary>
    /// What the handler did with the row.
    /// </summary>
    public UserImportRowOutcome Outcome { get; init; }

    /// <summary>
    /// Human-readable detail: the failure reason when
    /// <see cref="Outcome"/> is <see cref="UserImportRowOutcome.Failed"/>,
    /// otherwise empty.
    /// </summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Aggregate outcome of an <c>ImportUsersCommand</c>: the three tally
/// counts plus the per-row report (same SUCCESS-shaped contract as the
/// product import — only infrastructure failures produce a
/// <c>Result.Failure</c>).
/// </summary>
public sealed record UserImportResultDto
{
    /// <summary>
    /// How many rows created a new user.
    /// </summary>
    public int CreatedCount { get; init; }

    /// <summary>
    /// How many rows were skipped because the worker ID already exists.
    /// </summary>
    public int SkippedCount { get; init; }

    /// <summary>
    /// How many rows were rejected. Always equals
    /// <c>RowResults.Count(r => r.Outcome == Failed)</c>.
    /// </summary>
    public int FailedCount { get; init; }

    /// <summary>
    /// Per-row report in FILE ORDER.
    /// </summary>
    public IReadOnlyList<UserImportRowResultDto> RowResults { get; init; }
        = Array.Empty<UserImportRowResultDto>();
}
