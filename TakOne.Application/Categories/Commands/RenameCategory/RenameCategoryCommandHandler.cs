using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Categories.Commands.RenameCategory;

/// <summary>
/// Renames an existing top-level Category.
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class RenameCategoryCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        RenameCategoryCommand command,
        ICurrentUserService currentUser,
        ICategoryRepository categoryRepository,
        IUnitOfWork unitOfWork,
        ILogger<RenameCategoryCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("RenameCategory: unauthenticated call rejected.");

            return Result.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the Category. We don't need its hierarchy (no SubCategory
        //    mutations here), so the lightweight GetByIdAsync is enough.
        // ------------------------------------------------------------------
        var category = await categoryRepository.GetByIdAsync(command.CategoryId, cancellationToken);

        if (category is null)
        {
            logger.LogWarning
                ("RenameCategory: category {CategoryId} was not found. Requested by user {UserId}.",
                command.CategoryId, currentUser.UserId);

            return Result.Failure($"Category '{command.CategoryId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Name uniqueness — exclude this category's own ID so renaming
        //    to the same name (no-op rename) is allowed. Without the
        //    exclude, every rename would fail because the category would
        //    find its own name in the catalog.
        // ------------------------------------------------------------------
        var nameExistsForOther = await categoryRepository.NameExistsAsync(
            command.NewName, excludeId: category.Id, cancellationToken);

        if (nameExistsForOther)
        {
            logger.LogWarning
                ("RenameCategory: category name '{NewName}' already exists. Requested by user {UserId}.",
                command.NewName, currentUser.UserId);

            return Result.Failure
                ($"Another category with the name '{command.NewName}' already exists. " + "Choose a different name.");
        }

        // ------------------------------------------------------------------
        // 3. Delegate to the aggregate. Rename enforces:
        //      - name non-empty + length ≤ 100
        //    These are the domain's last-line-of-defense invariants; the
        //    validator already caught friendly-violations earlier.
        //    DomainException is caught by middleware and converted to
        //    Result.Failure.
        // ------------------------------------------------------------------
        category.Rename(command.NewName);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("RenameCategory: category {CategoryId} renamed to '{NewName}' by user {UserId}.",
            category.Id, category.Name, currentUser.UserId);

        return Result.Success();
    }
}