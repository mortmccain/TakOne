using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Customers.Entities;
using TakOne.Domain.Users;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Users.Commands.ImportUsers;

/// <summary>
/// Create-or-skip bulk user import — see the command's remarks.
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
/// <remarks>
/// <para>
/// <b>PER-ROW COMMITS</b>: unlike <c>CreateStaffCommandHandler</c> (one
/// user per command, one SaveChanges), this handler saves after EVERY
/// successful row. UserManager.CreateAsync flushes its own SaveChanges
/// internally, which also flushes any still-pending Domain Users from
/// previous rows — saving per row keeps the failure path surgical: when a
/// row's identity creation fails, the change tracker holds only THAT
/// row's Domain User, so <see cref="IUnitOfWork.ClearChangeTracker"/>
/// discards exactly the failed row (the same orphan-prevention pattern as
/// <c>CreateStaffCommandHandler</c>, adapted to the bulk case).
/// </para>
/// <para>
/// <b>KNOWN IDENTITY QUIRK (inherited, documented)</b>: if
/// <c>UserManager.AddToRoleAsync</c> fails INSIDE
/// <see cref="IUserAccountService.CreateIdentityAccountAsync"/> (only
/// possible when the role seed is broken), the identity user is deleted
/// by the service but the current row's Domain User row — already flushed
/// by UserManager's internal SaveChanges — would survive. This is the
/// same accepted quirk <c>CreateStaffCommandHandler</c> documents; the
/// practical mitigation is that all role names are pre-validated against
/// the seeded role set before the identity call is ever made.
/// </para>
/// <para>
/// <b>DDD NOTES</b>: users are created ONLY through the Domain factories
/// (<c>User.CreateCustomer</c> / <c>User.CreateStaff</c>), status changes
/// go through <c>User.Deactivate()</c> + the Identity-side
/// <c>SetUserActiveStatusAsync</c> (the two-table flag sync — see that
/// method's doc), and role assignment goes through the Identity
/// abstraction. The handler never pokes at table state directly.
/// </para>
/// </remarks>
public sealed class ImportUsersCommandHandler
{
    /// <summary>
    /// Hard cap on rows per import call — same value as the product
    /// import's cap.
    /// </summary>
    private const int MaxRowsPerImport = 2000;

    /// <summary>
    /// The complete set of ASP.NET Identity role names the system seeds.
    /// A CSV role outside this set is a row error (catches typos and
    /// locale-mangled role names before they reach Identity).
    /// </summary>
    private static readonly HashSet<string> KnownRoles = new(StringComparer.Ordinal)
    {
        Roles.Admin,
        Roles.Manager,
        Roles.Employee,
        Roles.ReadOnly,
        Roles.Customer
    };

    public static async Task<Result<UserImportResultDto>> HandleAsync
        (
        ImportUsersCommand command,
        ICurrentUserService currentUser,
        IUserRepository userRepository,
        ICustomerGroupRepository customerGroupRepository,
        IUserAccountService userAccountService,
        IUnitOfWork unitOfWork,
        ILogger<ImportUsersCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check. [RequireRoles(Admin)] already rejected
        //    non-admin callers; re-checking keeps the invariant honest.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("ImportUsers: unauthenticated call rejected.");
            return Result<UserImportResultDto>.Failure("Authentication required.");
        }

        if (!currentUser.IsInRole(Roles.Admin))
        {
            logger.LogWarning
                ("ImportUsers: caller {ActorId} is not an Admin. Rejected.", currentUser.UserId);
            return Result<UserImportResultDto>.Failure("Only administrators may import users.");
        }

        // ------------------------------------------------------------------
        // 1. Shape check (whole-command failures, not per-row).
        // ------------------------------------------------------------------
        if (command.Rows.Count == 0)
        {
            return Result<UserImportResultDto>.Failure("The import file contains no data rows.");
        }

        if (command.Rows.Count > MaxRowsPerImport)
        {
            return Result<UserImportResultDto>.Failure
                ($"The import file contains {command.Rows.Count} rows — the maximum is {MaxRowsPerImport}. Split the file and retry.");
        }

        // ------------------------------------------------------------------
        // 2. Load every customer group ONCE (name → aggregate) for the
        //    group-name resolution each customer row needs.
        // ------------------------------------------------------------------
        var groups = await customerGroupRepository.GetAllAsync(includeInactive: true, cancellationToken);

        // ------------------------------------------------------------------
        // 3. Process rows. Each row independently validates, creates its
        //    user, and COMMITS — see the class remarks for why per-row
        //    saves make the failure path surgical.
        // ------------------------------------------------------------------
        var rowResults = new List<UserImportRowResultDto>(command.Rows.Count);
        var created = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var row in command.Rows)
        {
            var result = await ProcessRowAsync
                (row, groups, userRepository, userAccountService, unitOfWork, cancellationToken);
            rowResults.Add(result.Result);

            switch (result.Outcome)
            {
                case UserImportRowOutcome.Created: created++; break;
                case UserImportRowOutcome.Skipped: skipped++; break;
                default: failed++; break;
            }
        }

        logger.LogInformation
            ("ImportUsers: {Created} created, {Skipped} skipped (already existed), {Failed} failed (requested by user {ActorId}).",
            created, skipped, failed, currentUser.UserId);

        return Result<UserImportResultDto>.Success(new UserImportResultDto
        {
            CreatedCount = created,
            SkippedCount = skipped,
            FailedCount = failed,
            RowResults = rowResults
        });
    }

    /// <summary>
    /// Validates + creates one row's user. Returns the row report plus the
    /// outcome (the tallies switch on it).
    /// </summary>
    private static async Task<(UserImportRowResultDto Result, UserImportRowOutcome Outcome)> ProcessRowAsync
        (
        UserImportRowDto row,
        List<CustomerGroup> groups,
        IUserRepository userRepository,
        IUserAccountService userAccountService,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken
        )
    {
        // ── Cheap per-row shape checks (no DB, no domain calls). ──
        if (string.IsNullOrWhiteSpace(row.WorkerId))
        {
            return (Failed(row, "Worker ID is empty."), UserImportRowOutcome.Failed);
        }

        if (string.IsNullOrWhiteSpace(row.FullName))
        {
            return (Failed(row, "Full name is empty."), UserImportRowOutcome.Failed);
        }

        if (row.Roles.Count == 0)
        {
            return (Failed(row, "No role was given (the Roles column is empty or unrecognized)."), UserImportRowOutcome.Failed);
        }

        var unknownRoles = row.Roles.Where(r => !KnownRoles.Contains(r)).ToList();
        if (unknownRoles.Count > 0)
        {
            return (Failed(row, $"Unknown role(s): {string.Join(", ", unknownRoles)}."), UserImportRowOutcome.Failed);
        }

        // ── Skip-if-exists (idempotent re-import). ──
        var existing = await userRepository.GetByWorkerIdAsync(row.WorkerId, cancellationToken);
        if (existing is not null)
        {
            return (new UserImportRowResultDto
            {
                RowNumber = row.RowNumber,
                WorkerId = row.WorkerId,
                Outcome = UserImportRowOutcome.Skipped
            }, UserImportRowOutcome.Skipped);
        }

        // ── Group resolution (customer rows only). ──
        Guid? groupId = null;
        if (!string.IsNullOrWhiteSpace(row.GroupName))
        {
            var group = groups.FirstOrDefault
                (g => string.Equals(g.Name, row.GroupName.Trim(), StringComparison.OrdinalIgnoreCase));

            if (group is null)
            {
                return (Failed(row, $"Customer group '{row.GroupName}' was not found."), UserImportRowOutcome.Failed);
            }

            groupId = group.Id;
        }

        // ── Default password: "@" + reversed worker ID + "Temp". ──
        var password = BuildDefaultPassword(row.WorkerId);

        // ── Domain user via the factories (staff vs customer by group). ──
        var user = groupId is null
            ? User.CreateStaff(row.WorkerId.Trim(), row.FullName.Trim(), row.Gender)
            : User.CreateCustomer(row.WorkerId.Trim(), row.FullName.Trim(), groupId.Value, row.Gender);

        await userRepository.AddAsync(user, cancellationToken);

        // ── Identity account (shared PK) + primary role. The FIRST role is
        //    the primary; the remaining roles are assigned below. ──
        var accountResult = await userAccountService.CreateIdentityAccountAsync
            (
            user.Id,
            user.WorkerId,
            email: string.Empty, // the export format carries no email column — empty means "leave Email null" (see the service's contract)
            password,
            row.Roles[0],
            user.Gender,
            cancellationToken
            );

        if (accountResult.IsFailure)
        {
            // Identity creation failed BEFORE any SaveChanges for this row
            // (or the service rolled the identity user back itself). The
            // change tracker holds ONLY this row's Domain User — every
            // prior row already committed — so clearing it discards
            // exactly the failed row.
            unitOfWork.ClearChangeTracker();

            return (Failed(row, accountResult.Error), UserImportRowOutcome.Failed);
        }

        // ── Extra roles (multi-role users, e.g. "Employee; Customer").
        //    AssignRoleAsync is idempotent per the interface contract. ──
        foreach (var extraRole in row.Roles.Skip(1))
        {
            var roleResult = await userAccountService.AssignRoleAsync(user.Id, extraRole, cancellationToken);
            if (roleResult.IsFailure)
            {
                // The account exists with the primary role — the row is a
                // creation with a warning-grade shortfall. Report failure
                // (the row did not fully import) and let the admin fix the
                // missing role via the user detail page.
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return (Failed(row, $"Account created, but assigning the extra role '{extraRole}' failed: {roleResult.Error}"), UserImportRowOutcome.Failed);
            }
        }

        // ── Inactive rows: deactivate BOTH flags (Domain + Identity) —
        //    the two-table sync UserDetail's deactivation flow performs. ──
        if (!row.IsActive)
        {
            user.Deactivate();
            var statusResult = await userAccountService.SetUserActiveStatusAsync(user.Id, isActive: false, cancellationToken);
            if (statusResult.IsFailure)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return (Failed(row, $"Account created, but deactivating it failed: {statusResult.Error}"), UserImportRowOutcome.Failed);
            }
        }

        // ── Commit the row (Domain user + deactivation flag flush here;
        //    UserManager's internal saves already wrote the identity
        //    rows). Per-row commit — see class remarks. ──
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return (new UserImportRowResultDto
        {
            RowNumber = row.RowNumber,
            WorkerId = row.WorkerId,
            Outcome = UserImportRowOutcome.Created
        }, UserImportRowOutcome.Created);
    }

    /// <summary>
    /// The default password formula for imported users:
    /// <c>"@" + REVERSED(workerId) + "Temp"</c> — e.g. worker id
    /// <c>12345</c> → <c>@54321Temp</c>. Combined with the forced
    /// one-time password change on first login, the importer never knows
    /// a password the user keeps.
    /// </summary>
    private static string BuildDefaultPassword(string workerId)
    {
        var reversed = new string(workerId.Trim().ToCharArray().Reverse().ToArray());
        return $"@{reversed}Temp";
    }

    /// <summary>
    /// Builds a Failed row report.
    /// </summary>
    private static UserImportRowResultDto Failed(UserImportRowDto row, string message)
        => new()
        {
            RowNumber = row.RowNumber,
            WorkerId = row.WorkerId,
            Outcome = UserImportRowOutcome.Failed,
            Message = message
        };
}
