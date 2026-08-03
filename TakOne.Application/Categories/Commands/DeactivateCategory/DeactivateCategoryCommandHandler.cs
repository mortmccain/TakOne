using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Categories.Commands.DeactivateCategory;

/// <summary>
/// Soft-deletes a top-level Category (cascade-deactivates children).
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class DeactivateCategoryCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        DeactivateCategoryCommand command,
        ICurrentUserService currentUser,
        ICategoryRepository categoryRepository,
        IUnitOfWork unitOfWork,
        ILogger<DeactivateCategoryCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("DeactivateCategory: unauthenticated call rejected.");

            return Result.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the Category. We need the full hierarchy because
        //    Deactivate cascades to SubCategories and SubSubCategories —
        //    EF Core must have them tracked for the cascade to persist.
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
                ("DeactivateCategory: category {CategoryId} was not found. Requested by user {UserId}.",
                command.CategoryId, currentUser.UserId);

            return Result.Failure($"Category '{command.CategoryId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Delegate to the aggregate. Deactivate cascades to SubCategories
        //    and SubSubCategories inside the aggregate. EF Core's change
        //    tracker will pick up every IsActive change in a single
        //    SaveChangesAsync transaction.
        // ------------------------------------------------------------------
        category.Deactivate();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("DeactivateCategory: category {CategoryId} ({Name}) deactivated (with cascade) by user {UserId}.",
            category.Id, category.Name, currentUser.UserId);

        return Result.Success();
    }
}