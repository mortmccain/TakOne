using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Categories.Entities;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Categories.Commands.SubcategoryCommands.CreateSubCategory;

/// <summary>
/// Adds a new SubCategory under an existing top-level Category.
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class CreateSubCategoryCommandHandler
{
    public static async Task<Result<Guid>> HandleAsync
        (
        CreateSubCategoryCommand command,
        ICurrentUserService currentUser,
        ICategoryRepository categoryRepository,
        IUnitOfWork unitOfWork,
        ILogger<CreateSubCategoryCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("CreateSubCategory: unauthenticated call rejected.");

            return Result<Guid>.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the parent Category WITH its hierarchy — AsNoTracking.
        //    We need the SubCategories collection loaded so the aggregate
        //    can validate name uniqueness against its existing children. But
        //    we do NOT want EF Core to track the parent or its existing
        //    children, because that's what causes the
        //    DbUpdateConcurrencyException.
        //
        //    THE BUG (previous fix that didn't work):
        //      unitOfWork.ClearChangeTracker();
        //      var category = await categoryRepository.GetByIdWithHierarchyAsync(...);
        //
        //    ClearChangeTracker detaches everything, but the subsequent
        //    TRACKED GetByIdWithHierarchyAsync call re-attaches the parent
        //    Category + all its existing SubCategories / SubSubCategories.
        //    When we then call category.AddSubCategory(name), EF Core's
        //    DetectChanges (which runs automatically inside SaveChanges)
        //    sees the _subCategories collection change and may mark the
        //    parent (or a sibling) as Modified. SaveChanges then issues
        //    a spurious UPDATE whose WHERE clause matches 0 rows →
        //    DbUpdateConcurrencyException: "expected 1, affected 0".
        //
        //    THE FIX:
        //    Load AsNoTracking so the parent + children are NEVER in the
        //    tracker. Mutating _subCategories in memory is then harmless
        //    (the tracker doesn't know about it). After AddSubCategory
        //    returns the new SubCategory, we explicitly tell the tracker
        //    "track ONLY this new entity" via unitOfWork.AddEntity(sub).
        //    SaveChanges then generates exactly ONE INSERT (for the new
        //    SubCategory) and ZERO UPDATEs — the parent and siblings are
        //    untracked, so they cannot be marked Modified.
        // ------------------------------------------------------------------
        var category = await categoryRepository.GetByIdWithHierarchyNoTrackingAsync
            (command.CategoryId, cancellationToken);

        if (category is null)
        {
            logger.LogWarning
                ("CreateSubCategory: parent category {CategoryId} was not found. Requested by user {UserId}.",
                command.CategoryId, currentUser.UserId);

            return Result<Guid>.Failure($"Category '{command.CategoryId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Delegate to the aggregate. AddSubCategory:
        //      - throws if the parent Category is deactivated
        //      - throws if a SubCategory with the same name already exists
        //        under this Category (intra-aggregate uniqueness, case-insensitive)
        //      - constructs the SubCategory with the parent's Id
        //      - appends it to the SubCategories collection (in memory only —
        //        the parent is untracked, so this has no DB effect)
        // ------------------------------------------------------------------
        SubCategory subCategory;
        try
        {
            subCategory = category.AddSubCategory(command.Name);
        }
        catch (DomainException ex)
        {
            logger.LogWarning
                ("CreateSubCategory: aggregate rejected creation under category {CategoryId}. Reason: {Reason}. Requested by user {UserId}.",
                command.CategoryId, ex.Message, currentUser.UserId);

            return Result<Guid>.Failure(ex.Message);
        }

        // ------------------------------------------------------------------
        // 3. Explicitly track ONLY the new SubCategory. The parent Category
        //    and its existing children stay untracked (AsNoTracking), so
        //    SaveChanges cannot generate any UPDATE for them.
        // ------------------------------------------------------------------
        unitOfWork.AddEntity(subCategory);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("CreateSubCategory: SubCategory {SubCategoryId} ({Name}) created under category {CategoryId} by user {UserId}.",
            subCategory.Id, subCategory.Name, category.Id, currentUser.UserId);

        return Result<Guid>.Success(subCategory.Id);
    }
}