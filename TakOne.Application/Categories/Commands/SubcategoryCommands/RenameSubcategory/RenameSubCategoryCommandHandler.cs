using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Categories.Commands.RenameSubCategory;

/// <summary>
/// Renames an existing SubCategory (via its parent Category aggregate).
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class RenameSubCategoryCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        RenameSubCategoryCommand command,
        ICurrentUserService currentUser,
        ICategoryRepository categoryRepository,
        IUnitOfWork unitOfWork,
        ILogger<RenameSubCategoryCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("RenameSubCategory: unauthenticated call rejected.");

            return Result.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the parent Category WITH its hierarchy. RenameSubCategory
        //    needs to (a) look up the target SubCategory, and (b) validate
        //    name uniqueness against siblings — both require the children
        //    to be loaded.
        // ------------------------------------------------------------------
        var category = await categoryRepository.GetByIdWithHierarchyAsync(command.CategoryId, cancellationToken);
        if (category is null)
        {
            logger.LogWarning
                ("RenameSubCategory: parent category {CategoryId} was not found. Requested by user {UserId}.",
                command.CategoryId, currentUser.UserId);

            return Result.Failure($"Category '{command.CategoryId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Delegate to the aggregate. RenameSubCategory:
        //      - throws if the parent Category is deactivated
        //      - throws if the SubCategoryId does not exist under this Category
        //      - throws if a sibling SubCategory already has the new name
        //        (case-insensitive, excluding the renamed one's own Id)
        //    DomainException is caught by middleware.
        // ------------------------------------------------------------------
        try
        {
            category.RenameSubCategory(command.SubCategoryId, command.NewName);
        }
        catch (DomainException ex)
        {
            logger.LogWarning
                ("RenameSubCategory: aggregate rejected rename of {SubCategoryId} under category {CategoryId}. Reason: {Reason}. Requested by user {UserId}.",
                command.SubCategoryId, command.CategoryId, ex.Message, currentUser.UserId);

            return Result.Failure(ex.Message);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("RenameSubCategory: SubCategory {SubCategoryId} under category {CategoryId} renamed to '{NewName}' by user {UserId}.",
            command.SubCategoryId, command.CategoryId, command.NewName, currentUser.UserId);

        return Result.Success();
    }
}