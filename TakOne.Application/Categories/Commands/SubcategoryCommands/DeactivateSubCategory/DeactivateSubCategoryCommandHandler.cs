using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Categories.Commands.DeactivateSubCategory;

/// <summary>
/// Soft-deletes a SubCategory (cascade-deactivates SubSubCategories).
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class DeactivateSubCategoryCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        DeactivateSubCategoryCommand command,
        ICurrentUserService currentUser,
        ICategoryRepository categoryRepository,
        IUnitOfWork unitOfWork,
        ILogger<DeactivateSubCategoryCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("DeactivateSubCategory: unauthenticated call rejected.");

            return Result.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the parent Category WITH hierarchy. DeactivateSubCategory
        //    cascades to SubSubCategories, so EF Core must have the full
        //    tree tracked for the cascade to persist in one transaction.
        // ------------------------------------------------------------------
        var category = await categoryRepository.GetByIdWithHierarchyAsync(command.CategoryId, cancellationToken);

        if (category is null)
        {
            logger.LogWarning
                ("DeactivateSubCategory: parent category {CategoryId} was not found. Requested by user {UserId}.",
                command.CategoryId, currentUser.UserId);

            return Result.Failure($"Category '{command.CategoryId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Delegate to the aggregate. DeactivateSubCategory:
        //      - throws if the SubCategoryId does not exist under this Category
        //      - sets the SubCategory's IsActive = false
        //      - cascades: sets all SubSubCategories' IsActive = false
        //    EF Core's change tracker will pick up every IsActive change
        //    in a single SaveChangesAsync transaction.
        // ------------------------------------------------------------------
        try
        {
            category.DeactivateSubCategory(command.SubCategoryId);
        }
        catch (DomainException ex)
        {
            logger.LogWarning
                ("DeactivateSubCategory: aggregate rejected deactivation of {SubCategoryId} under category {CategoryId}. Reason: {Reason}. Requested by user {UserId}.",
                command.SubCategoryId, command.CategoryId, ex.Message, currentUser.UserId);
            return Result.Failure(ex.Message);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("DeactivateSubCategory: SubCategory {SubCategoryId} under category {CategoryId} deactivated (with cascade) by user {UserId}.",
            command.SubCategoryId, command.CategoryId, currentUser.UserId);

        return Result.Success();
    }
}