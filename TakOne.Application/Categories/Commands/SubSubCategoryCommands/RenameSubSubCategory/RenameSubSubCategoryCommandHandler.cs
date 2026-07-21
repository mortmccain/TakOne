using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Categories.Commands.SubSubCategoryCommands.RenameSubSubCategory;

/// <summary>
/// Renames an existing SubSubCategory (via its parent chain).
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class RenameSubSubCategoryCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        RenameSubSubCategoryCommand command,
        ICurrentUserService currentUser,
        ICategoryRepository categoryRepository,
        IUnitOfWork unitOfWork,
        ILogger<RenameSubSubCategoryCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("RenameSubSubCategory: unauthenticated call rejected.");

            return Result.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the parent Category WITH full hierarchy. RenameSubSubCategory
        //    needs to look up the target SubCategory AND the target
        //    SubSubCategory by Id — both require the children to be loaded.
        // ------------------------------------------------------------------
        var category = await categoryRepository.GetByIdWithHierarchyAsync(command.CategoryId, cancellationToken);

        if (category is null)
        {
            logger.LogWarning
                ("RenameSubSubCategory: parent category {CategoryId} was not found. Requested by user {UserId}.",
                command.CategoryId, currentUser.UserId);

            return Result.Failure($"Category '{command.CategoryId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Delegate to the aggregate. RenameSubSubCategory:
        //      - throws if the parent Category is deactivated
        //      - throws if the SubCategoryId does not exist
        //      - throws if the SubCategory is deactivated
        //      - throws if the SubSubCategoryId does not exist under the SubCategory
        //      - throws if a sibling SubSubCategory already has the new name
        //        (case-insensitive, excluding the renamed one's own Id)
        //    DomainException is caught by middleware.
        // ------------------------------------------------------------------
        try
        {
            category.RenameSubSubCategory(command.SubCategoryId, command.SubSubCategoryId, command.NewName);
        }
        catch (DomainException ex)
        {
            logger.LogWarning
                ("RenameSubSubCategory: aggregate rejected rename of {SubSubCategoryId} under SubCategory {SubCategoryId} (category {CategoryId}). Reason: {Reason}. Requested by user {UserId}.",
                command.SubSubCategoryId, command.SubCategoryId, command.CategoryId, ex.Message, currentUser.UserId);

            return Result.Failure(ex.Message);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("RenameSubSubCategory: SubSubCategory {SubSubCategoryId} under SubCategory {SubCategoryId} (category {CategoryId}) renamed to '{NewName}' by user {UserId}.",
            command.SubSubCategoryId, command.SubCategoryId, command.CategoryId, command.NewName, currentUser.UserId);

        return Result.Success();
    }
}