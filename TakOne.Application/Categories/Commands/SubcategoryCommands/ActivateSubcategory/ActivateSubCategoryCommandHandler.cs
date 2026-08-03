using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Categories.Commands.ActivateSubCategory;

/// <summary>
/// Reactivates a deactivated SubCategory.
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class ActivateSubCategoryCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        ActivateSubCategoryCommand command,
        ICurrentUserService currentUser,
        ICategoryRepository categoryRepository,
        IUnitOfWork unitOfWork,
        ILogger<ActivateSubCategoryCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("ActivateSubCategory: unauthenticated call rejected.");

            return Result.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the parent Category WITH hierarchy. ActivateSubCategory
        //    needs to look up the target SubCategory by Id — that requires
        //    the children to be loaded.
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
                ("ActivateSubCategory: parent category {CategoryId} was not found. Requested by user {UserId}.",
                command.CategoryId, currentUser.UserId);

            return Result.Failure($"Category '{command.CategoryId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Delegate to the aggregate. ActivateSubCategory:
        //      - throws if the SubCategoryId does not exist under this Category
        //      - sets the SubCategory's IsActive = true (idempotent)
        //    DomainException is caught by middleware.
        // ------------------------------------------------------------------
        try
        {
            category.ActivateSubCategory(command.SubCategoryId);
        }
        catch (DomainException ex)
        {
            logger.LogWarning
                ("ActivateSubCategory: aggregate rejected activation of {SubCategoryId} under category {CategoryId}. Reason: {Reason}. Requested by user {UserId}.",
                command.SubCategoryId, command.CategoryId, ex.Message, currentUser.UserId);

            return Result.Failure(ex.Message);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("ActivateSubCategory: SubCategory {SubCategoryId} under category {CategoryId} activated by user {UserId}.",
            command.SubCategoryId, command.CategoryId, currentUser.UserId);

        return Result.Success();
    }
}