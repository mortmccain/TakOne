using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Categories.Commands.ActivateCategory;

/// <summary>
/// Reactivates a deactivated top-level Category.
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class ActivateCategoryCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        ActivateCategoryCommand command,
        ICurrentUserService currentUser,
        ICategoryRepository categoryRepository,
        IUnitOfWork unitOfWork,
        ILogger<ActivateCategoryCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("ActivateCategory: unauthenticated call rejected.");

            return Result.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the Category. No hierarchy needed — Activate only touches
        //    the root.
        //
        //    ClearChangeTracker FIRST: prevents the Blazor Server scoped-
        //    DbContext stale-tracking bug (see CreateSubCategoryCommandHandler
        //    for the full rationale).
        // ------------------------------------------------------------------
        unitOfWork.ClearChangeTracker();
        var category = await categoryRepository.GetByIdAsync(command.CategoryId, cancellationToken);

        if (category is null)
        {
            logger.LogWarning
                ("ActivateCategory: category {CategoryId} was not found. Requested by user {UserId}.",
                command.CategoryId, currentUser.UserId);

            return Result.Failure($"Category '{command.CategoryId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Delegate to the aggregate. Activate is idempotent — does NOT
        //    cascade to SubCategories (see command XML docs for rationale).
        // ------------------------------------------------------------------
        category.Activate();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("ActivateCategory: category {CategoryId} ({Name}) activated by user {UserId}.",
            category.Id, category.Name, currentUser.UserId);

        return Result.Success();
    }
}