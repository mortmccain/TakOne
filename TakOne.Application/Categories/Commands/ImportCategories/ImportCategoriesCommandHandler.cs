using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Categories.Entities;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Categories.Commands.ImportCategories;

/// <summary>
/// Handler for <see cref="ImportCategoriesCommand"/> — a find-or-create
/// (upsert) walk over the CSV rows against the FULL existing tree.
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
/// <remarks>
/// <para>
/// <b>BEST-EFFORT, PER-ROW REPORT</b>: an invalid row fails ITSELF (with
/// a reason in the result) but never aborts the other rows — the same
/// contract as <c>ImportProductsCommandHandler</c>. A row's chain is
/// resolved left-to-right (Category → Sub → SubSub); each missing node
/// in a chain is created through the AGGREGATE's methods so every
/// intra-aggregate invariant (name uniqueness among siblings, active
/// parent required for children) is enforced by the domain, not
/// re-implemented here.
/// </para>
/// <para>
/// <b>PURE WALK, THEN BATCHED PERSISTENCE</b>: the row walk is entirely
/// in-memory (the loaded tree + the nodes created during the run) —
/// nothing touches the change tracker mid-walk. AFTER the walk, new
/// ROOT categories are registered via
/// <see cref="ICategoryRepository.AddAsync"/> (their new children ride
/// the Added graph), and new children under PRE-EXISTING categories are
/// registered individually via <see cref="IUnitOfWork.AddEntity"/> — the
/// documented stale-tracking-safe pattern (the tree itself was loaded
/// <c>AsNoTracking</c> via
/// <see cref="ICategoryRepository.GetAllWithHierarchyNoTrackingAsync"/>,
/// so SaveChanges can only ever emit INSERTs, never a spurious UPDATE).
/// One <see cref="IUnitOfWork.SaveChangesAsync"/> at the end commits
/// every created node in a SINGLE transaction: either the whole run's
/// creations persist or none do.
/// </para>
/// <para>
/// <b>NO PARTIAL-ROW PERSISTENCE</b>: a row can never leave half its
/// chain behind when a later cell fails. The failure modes are
/// exhaustive: the Category cell fails before anything is created
/// (empty name); a deactivated EXISTING parent rejects children inside
/// the aggregate's EnsureActive guards BEFORE constructing the child;
/// and name clashes among siblings never reach the aggregate's add
/// methods because the walk only creates a node when the name was NOT
/// found among the (in-memory) siblings.
/// </para>
/// <para>
/// <b>DEACTIVATED MARKERS</b>: the export appends " (deactivated)" /
/// " (غیرفعال)" to deactivated nodes' names so a round-tripped file
/// matches the tree. This handler strips a TRAILING marker from every
/// incoming name before matching (the same twin-constant approach as
/// ImportProductsCommandHandler — kept in sync with the
/// AdminCategories resx "DeactivatedSuffix" keys).
/// </para>
/// <para>
/// <b>CROSS-AGGREGATE UNIQUENESS</b>: category names are unique across
/// the catalog (DB unique index; the handler pre-empts it by matching
/// against the loaded tree case-insensitively). Two rows in the SAME
/// file that create the same new category resolve to the SAME in-memory
/// instance — the second row reports "existed" relative to the first. A
/// concurrent creation between our load and our save surfaces as the
/// DB's unique-index violation and fails the whole command loudly
/// (retry re-runs the idempotent walk) — the same trade-off
/// CreateCategoryCommandHandler documents.
/// </para>
/// </remarks>
public sealed class ImportCategoriesCommandHandler
{
    /// <summary>
    /// Hard cap on rows per import call — the products import's value,
    /// for the same reason: a categories tree past 2,000 path rows is
    /// pathological, and the cap bounds the handler's in-memory walk.
    /// </summary>
    private const int MaxRowsPerImport = 2000;

    /// <summary>
    /// Deactivation markers the export appends to category names. The
    /// matcher strips a TRAILING occurrence of either variant before
    /// comparing. Kept in sync with AdminCategories.en-US.resx /
    /// .fa-IR.resx ("DeactivatedSuffix") — the same convention as
    /// ImportProductsCommandHandler's twin constants.
    /// </summary>
    private static readonly string[] DeactivatedSuffixes =
    {
        " (deactivated)",
        " (غیرفعال)"
    };

    public static async Task<Result<CategoryImportResultDto>> HandleAsync(
        ImportCategoriesCommand command,
        ICurrentUserService currentUser,
        ICategoryRepository categoryRepository,
        IUnitOfWork unitOfWork,
        ILogger<ImportCategoriesCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        // ── 0. Defense-in-depth auth check. [RequireRoles(Admin)] already
        //    rejected non-admin callers via Wolverine's
        //    AuthorizationMiddleware; re-checking keeps the invariant
        //    honest for non-HTTP invocations.
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("ImportCategories: unauthenticated call rejected.");
            return Result<CategoryImportResultDto>.Failure("Authentication required.");
        }

        if (!currentUser.IsInRole(Roles.Admin))
        {
            logger.LogWarning("ImportCategories: caller {ActorId} is not an Admin. Rejected.",
                currentUser.UserId);
            return Result<CategoryImportResultDto>.Failure("Only administrators may import categories.");
        }

        // ── 1. Shape checks. Empty/gigantic files are caller errors, not
        //    per-row errors — fail the whole command with a clear message.
        if (command.Rows.Count == 0)
        {
            return Result<CategoryImportResultDto>.Failure("The import file contains no data rows.");
        }

        if (command.Rows.Count > MaxRowsPerImport)
        {
            return Result<CategoryImportResultDto>.Failure(
                $"The import file contains {command.Rows.Count} rows — the maximum is {MaxRowsPerImport}. Split the file and retry.");
        }

        // ── 2. Load the FULL existing tree ONCE, AsNoTracking (see the
        //    class remarks — the stale-tracking-safe pattern). The lookup
        //    map carries every existing top-level Category by trimmed name.
        var existingCategories = await categoryRepository.GetAllWithHierarchyNoTrackingAsync(cancellationToken);
        var categoriesByName = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in existingCategories)
        {
            categoriesByName[category.Name.Trim()] = category;
        }

        // Persistence batches, filled during the walk and drained AFTER
        // it (nothing touches the change tracker mid-walk):
        //   - new root categories (their new children ride the Added graph)
        //   - new children created under PRE-EXISTING (untracked) categories
        var newRoots = new List<Category>();
        var newChildrenOfExisting = new List<object>();
        var createdThisRun = new HashSet<Guid>();

        var rowResults = new List<CategoryImportRowResultDto>(command.Rows.Count);
        var created = 0;
        var existed = 0;
        var failed = 0;

        // ── 3. Resolve + apply each row independently (pure in-memory).
        foreach (var row in command.Rows)
        {
            var (result, outcome) = ProcessRow(
                row, categoriesByName, newRoots, newChildrenOfExisting, createdThisRun);
            rowResults.Add(result);

            if (outcome == CategoryImportRowOutcome.Created) created++;
            else if (outcome == CategoryImportRowOutcome.Existed) existed++;
            else failed++;
        }

        // ── 4. Register every created node with the change tracker, then
        //    persist all of it in ONE SaveChanges (single transaction —
        //    Wolverine's unit-of-work middleware commits it). The load was
        //    AsNoTracking, so this can only emit INSERTs.
        foreach (var root in newRoots)
        {
            await categoryRepository.AddAsync(root, cancellationToken);
        }

        foreach (var child in newChildrenOfExisting)
        {
            unitOfWork.AddEntity(child);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "ImportCategories: {Created} created, {Existed} existed, {Failed} failed (requested by user {ActorId}).",
            created, existed, failed, currentUser.UserId);

        return Result<CategoryImportResultDto>.Success(new CategoryImportResultDto
        {
            CreatedCount = created,
            ExistedCount = existed,
            FailedCount = failed,
            RowResults = rowResults
        });
    }

    /// <summary>
    /// Resolves + applies one row's Category → Sub → SubSub chain IN
    /// MEMORY: creates missing nodes via the aggregate (which enforces
    /// the intra-aggregate invariants) and registers them in the
    /// persistence batches. No DB and no change-tracker contact.
    /// </summary>
    private static (CategoryImportRowResultDto Result, CategoryImportRowOutcome Outcome) ProcessRow(
        CategoryImportRowDto row,
        Dictionary<string, Category> categoriesByName,
        List<Category> newRoots,
        List<object> newChildrenOfExisting,
        HashSet<Guid> createdThisRun)
    {
        // ── Per-row shape checks (cheap, no domain calls). ──
        var categoryName = StripDeactivatedMarker(row.CategoryName);
        if (string.IsNullOrWhiteSpace(categoryName))
        {
            return (Failed(row, "Category name is empty."), CategoryImportRowOutcome.Failed);
        }

        var subName = StripDeactivatedMarker(row.SubCategoryName);
        var subSubName = StripDeactivatedMarker(row.SubSubCategoryName);

        // A SubSub without a Sub is a contradiction — reject early (the
        // products import's rule; the chain simply cannot be expressed).
        if (string.IsNullOrWhiteSpace(subName) && !string.IsNullOrWhiteSpace(subSubName))
        {
            return (Failed(row, "A sub-sub-category was given without a sub-category."),
                CategoryImportRowOutcome.Failed);
        }

        try
        {
            var createdAny = false;

            // ── Level 1: Category (find-or-create). ──
            if (!categoriesByName.TryGetValue(categoryName, out var category))
            {
                category = Category.Create(categoryName);
                categoriesByName[categoryName] = category;
                newRoots.Add(category);
                createdThisRun.Add(category.Id);
                createdAny = true;
            }

            // Row references only the top-level Category — done.
            if (string.IsNullOrWhiteSpace(subName))
            {
                return (Done(row, createdAny),
                    createdAny ? CategoryImportRowOutcome.Created : CategoryImportRowOutcome.Existed);
            }

            // ── Level 2: SubCategory (find-or-create under the category). ──
            // The aggregate's AddSubCategory enforces sibling uniqueness +
            // active parent; the walk only calls it when the name is NOT
            // already among the (in-memory) siblings.
            var sub = category.SubCategories.FirstOrDefault(
                s => string.Equals(s.Name.Trim(), subName, StringComparison.OrdinalIgnoreCase));

            if (sub is null)
            {
                sub = category.AddSubCategory(subName);

                if (!createdThisRun.Contains(category.Id))
                {
                    // Pre-existing (untracked) category — the new child is
                    // registered individually (the AddEntity pattern).
                    newChildrenOfExisting.Add(sub);
                }
                // else: the category itself is a to-be-Added root — its new
                // child rides the root's graph when AddAsync registers it.

                createdAny = true;
            }

            // Row references Category + Sub — done.
            if (string.IsNullOrWhiteSpace(subSubName))
            {
                return (Done(row, createdAny),
                    createdAny ? CategoryImportRowOutcome.Created : CategoryImportRowOutcome.Existed);
            }

            // ── Level 3: SubSubCategory (find-or-create under the sub). ──
            var subSub = sub.SubSubCategories.FirstOrDefault(
                ss => string.Equals(ss.Name.Trim(), subSubName, StringComparison.OrdinalIgnoreCase));

            if (subSub is null)
            {
                subSub = category.AddSubSubCategory(sub.Id, subSubName);

                if (!createdThisRun.Contains(category.Id))
                {
                    newChildrenOfExisting.Add(subSub);
                }

                createdAny = true;
            }

            return (Done(row, createdAny),
                createdAny ? CategoryImportRowOutcome.Created : CategoryImportRowOutcome.Existed);
        }
        catch (DomainException ex)
        {
            // The aggregate rejected the row (deactivated parent etc.)
            // BEFORE constructing anything for the failing level — see the
            // class remarks for why no partial chain can leak from here.
            return (Failed(row, ex.Message), CategoryImportRowOutcome.Failed);
        }
    }

    /// <summary>
    /// Builds a Failed row report. The message carries the row's 1-based
    /// line number in the Settings card's ledger so the admin can jump
    /// straight to the offending line in the CSV.
    /// </summary>
    private static CategoryImportRowResultDto Failed(CategoryImportRowDto row, string message)
        => new()
        {
            RowNumber = row.RowNumber,
            CategoryName = StripDeactivatedMarker(row.CategoryName) ?? string.Empty,
            Outcome = CategoryImportRowOutcome.Failed,
            Message = message
        };

    /// <summary>
    /// Builds a Created/Existed row report.
    /// </summary>
    private static CategoryImportRowResultDto Done(CategoryImportRowDto row, bool createdAny)
        => new()
        {
            RowNumber = row.RowNumber,
            CategoryName = StripDeactivatedMarker(row.CategoryName) ?? string.Empty,
            Outcome = createdAny ? CategoryImportRowOutcome.Created : CategoryImportRowOutcome.Existed
        };

    /// <summary>
    /// Strips a TRAILING deactivated marker from an incoming CSV name so
    /// round-tripped exports match their tree. Null/whitespace-safe.
    /// </summary>
    private static string? StripDeactivatedMarker(string? name)
    {
        var normalized = name?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return normalized;
        }

        foreach (var suffix in DeactivatedSuffixes)
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return normalized[..^suffix.Length].TrimEnd();
            }
        }

        return normalized;
    }
}
