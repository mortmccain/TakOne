using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Categories.Commands.SubSubCategoryCommands.CreateSubSubCategory;

/// <summary>
/// Adds a new SubSubCategory under an existing SubCategory.
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class CreateSubSubCategoryCommandHandler
{
    public static async Task<Result<Guid>> HandleAsync
        (
        CreateSubSubCategoryCommand command,
        ICurrentUserService currentUser,
        ICategoryRepository categoryRepository,
        IUnitOfWork unitOfWork,
        ILogger<CreateSubSubCategoryCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("CreateSubSubCategory: unauthenticated call rejected.");

            return Result<Guid>.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the parent Category WITH its full hierarchy — AsNoTracking.
        //    The aggregate needs to (a) look up the target SubCategory,
        //    (b) check that the SubCategory is active, and (c) validate the
        //    new name against the SubCategory's existing SubSubCategories.
        //    All three require the children to be loaded into memory, but
        //    we do NOT want EF Core to track them.
        //
        //    THE BUG (previous fix that didn't work):
        //      unitOfWork.ClearChangeTracker();
        //      var category = await categoryRepository.GetByIdWithHierarchyAsync(...);
        //
        //    ClearChangeTracker detaches everything, but the subsequent
        //    TRACKED GetByIdWithHierarchyAsync call re-attaches the parent
        //    Category + all its existing SubCategories / SubSubCategories.
        //    When we then call category.AddSubSubCategory(...), EF Core's
        //    DetectChanges (which runs automatically inside SaveChanges)
        //    sees the _subSubCategories collection change and may mark the
        //    parent SubCategory (or a sibling) as Modified. SaveChanges
        //    then issues a spurious UPDATE whose WHERE clause matches 0
        //    rows → DbUpdateConcurrencyException: "expected 1, affected 0".
        //
        //    THE FIX:
        //    Load AsNoTracking so the parent + children are NEVER in the
        //    tracker. After AddSubSubCategory returns the new SubSubCategory,
        //    we explicitly tell the tracker "track ONLY this new entity"
        //    via unitOfWork.AddEntity(subSub). SaveChanges then generates
        //    exactly ONE INSERT and ZERO UPDATEs.
        //
        //    See CreateSubCategoryCommandHandler for the full rationale.
        // ------------------------------------------------------------------
        var category = await categoryRepository.GetByIdWithHierarchyNoTrackingAsync
            (command.CategoryId, cancellationToken);

        if (category is null)
        {
            logger.LogWarning
                ("CreateSubSubCategory: parent category {CategoryId} was not found. Requested by user {UserId}.",
                command.CategoryId, currentUser.UserId);

            return Result<Guid>.Failure($"Category '{command.CategoryId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Delegate to the aggregate. AddSubSubCategory:
        //      - throws if the parent Category is deactivated
        //      - throws if the SubCategoryId does not exist under this Category
        //      - throws if the SubCategory is deactivated
        //      - throws if a sibling SubSubCategory already has the new name
        //        (case-insensitive)
        //      - constructs the SubSubCategory with the parent SubCategory's Id
        //      - appends it to the SubCategory's SubSubCategories collection
        //        (in memory only — the parent is untracked, so this has no
        //        DB effect)
        //    DomainException is caught and converted to Result.Failure so
        //    the API can surface a friendly error message.
        // ------------------------------------------------------------------
        Domain.Categories.Entities.SubSubCategory subSubCategory;
        try
        {
            subSubCategory = category.AddSubSubCategory(command.SubCategoryId, command.Name);
        }
        catch (DomainException ex)
        {
            logger.LogWarning
                ("CreateSubSubCategory: aggregate rejected creation under SubCategory {SubCategoryId} (category {CategoryId}). Reason: {Reason}. Requested by user {UserId}.",
                command.SubCategoryId, command.CategoryId, ex.Message, currentUser.UserId);

            return Result<Guid>.Failure(ex.Message);
        }

        // ------------------------------------------------------------------
        // 3. Explicitly track ONLY the new SubSubCategory. The parent
        //    Category, the parent SubCategory, and all existing siblings
        //    stay untracked (AsNoTracking), so SaveChanges cannot generate
        //    any UPDATE for them.
        // ------------------------------------------------------------------
        unitOfWork.AddEntity(subSubCategory);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("CreateSubSubCategory: SubSubCategory {SubSubCategoryId} ({Name}) created under SubCategory {SubCategoryId} (category {CategoryId}) by user {UserId}.",
            subSubCategory.Id, subSubCategory.Name, command.SubCategoryId, command.CategoryId, currentUser.UserId);

        return Result<Guid>.Success(subSubCategory.Id);
    }
}