using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Categories.Commands.SubSubCategoryCommands.DeactivateSubSubCategory;

/// <summary>
/// Soft-deletes a SubSubCategory.
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class DeactivateSubSubCategoryCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        DeactivateSubSubCategoryCommand command,
        ICurrentUserService currentUser,
        ICategoryRepository categoryRepository,
        IUnitOfWork unitOfWork,
        ILogger<DeactivateSubSubCategoryCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("DeactivateSubSubCategory: unauthenticated call rejected.");

            return Result.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the parent Category WITH full hierarchy. The aggregate
        //    walks Category → SubCategory → SubSubCategory to look up the
        //    target. No cascade here (SubSubCategory is a leaf in the
        //    hierarchy), so we only need the tree loaded for the lookup.
        // ------------------------------------------------------------------
        var category = await categoryRepository.GetByIdWithHierarchyAsync(command.CategoryId, cancellationToken);
        if (category is null)
        {
            logger.LogWarning
                ("DeactivateSubSubCategory: parent category {CategoryId} was not found. Requested by user {UserId}.",
                command.CategoryId, currentUser.UserId);

            return Result.Failure($"Category '{command.CategoryId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Delegate to the aggregate. DeactivateSubSubCategory:
        //      - throws if the SubCategoryId does not exist
        //      - throws if the SubSubCategoryId does not exist under the SubCategory
        //      - sets the SubSubCategory's IsActive = false (idempotent)
        //    DomainException is caught by middleware.
        // ------------------------------------------------------------------
        try
        {
            category.DeactivateSubSubCategory(command.SubCategoryId, command.SubSubCategoryId);
        }
        catch (DomainException ex)
        {
            logger.LogWarning
                ("DeactivateSubSubCategory: aggregate rejected deactivation of {SubSubCategoryId} under SubCategory {SubCategoryId} (category {CategoryId}). Reason: {Reason}. Requested by user {UserId}.",
                command.SubSubCategoryId, command.SubCategoryId, command.CategoryId, ex.Message, currentUser.UserId);

            return Result.Failure(ex.Message);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("DeactivateSubSubCategory: SubSubCategory {SubSubCategoryId} under SubCategory {SubCategoryId} (category {CategoryId}) deactivated by user {UserId}.",
            command.SubSubCategoryId, command.SubCategoryId, command.CategoryId, currentUser.UserId);

        return Result.Success();
    }
}