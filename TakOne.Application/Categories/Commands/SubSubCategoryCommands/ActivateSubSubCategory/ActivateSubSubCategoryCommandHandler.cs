using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Categories.Commands.SubSubCategoryCommands.ActivateSubSubCategory;

/// <summary>
/// Reactivates a deactivated SubSubCategory.
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class ActivateSubSubCategoryCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        ActivateSubSubCategoryCommand command,
        ICurrentUserService currentUser,
        ICategoryRepository categoryRepository,
        IUnitOfWork unitOfWork,
        ILogger<ActivateSubSubCategoryCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("ActivateSubSubCategory: unauthenticated call rejected.");

            return Result.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the parent Category WITH full hierarchy. The aggregate
        //    needs to walk Category → SubCategory → SubSubCategory to look
        //    up the target.
        //
        //    ClearChangeTracker FIRST: prevents the Blazor Server scoped-
        //    DbContext stale-tracking bug (see CreateSubCategoryCommandHandler
        //    for the full rationale).
        // ------------------------------------------------------------------
        unitOfWork.ClearChangeTracker();
        var category = await categoryRepository.GetByIdWithHierarchyAsync(command.CategoryId, cancellationToken);

        if (category is null)
        {
            logger.LogWarning
                ("ActivateSubSubCategory: parent category {CategoryId} was not found. Requested by user {UserId}.",
                command.CategoryId, currentUser.UserId);

            return Result.Failure($"Category '{command.CategoryId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Delegate to the aggregate. ActivateSubSubCategory:
        //      - throws if the SubCategoryId does not exist
        //      - throws if the SubSubCategoryId does not exist under the SubCategory
        //      - sets the SubSubCategory's IsActive = true (idempotent)
        //    DomainException is caught by middleware.
        // ------------------------------------------------------------------
        try
        {
            category.ActivateSubSubCategory(command.SubCategoryId, command.SubSubCategoryId);
        }
        catch (DomainException ex)
        {
            logger.LogWarning
                ("ActivateSubSubCategory: aggregate rejected activation of {SubSubCategoryId} under SubCategory {SubCategoryId} (category {CategoryId}). Reason: {Reason}. Requested by user {UserId}.",
                command.SubSubCategoryId, command.SubCategoryId, command.CategoryId, ex.Message, currentUser.UserId);

            return Result.Failure(ex.Message);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("ActivateSubSubCategory: SubSubCategory {SubSubCategoryId} under SubCategory {SubCategoryId} (category {CategoryId}) activated by user {UserId}.",
            command.SubSubCategoryId, command.SubCategoryId, command.CategoryId, currentUser.UserId);

        return Result.Success();
    }
}