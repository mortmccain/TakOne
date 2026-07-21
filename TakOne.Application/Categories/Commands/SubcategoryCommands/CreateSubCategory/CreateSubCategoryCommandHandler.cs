using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Categories.Entities;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Categories.Commands.SubcategoryCommands.CreateSubCategory;

/// <summary>
/// Adds a new SubCategory under an existing top-level Category.
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class CreateSubCategoryCommandHandler
{
    public static async Task<Result<Guid>> HandleAsync
        (
        CreateSubCategoryCommand command,
        ICurrentUserService currentUser,
        ICategoryRepository categoryRepository,
        IUnitOfWork unitOfWork,
        ILogger<CreateSubCategoryCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("CreateSubCategory: unauthenticated call rejected.");

            return Result<Guid>.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the parent Category WITH its hierarchy. We need the
        //    SubCategories collection loaded so the aggregate can validate
        //    name uniqueness against its existing children. The lightweight
        //    GetByIdAsync returns a stub without children — using it here
        //    would silently let duplicate names through.
        // ------------------------------------------------------------------
        var category = await categoryRepository.GetByIdWithHierarchyAsync(command.CategoryId, cancellationToken);

        if (category is null)
        {
            logger.LogWarning
                ("CreateSubCategory: parent category {CategoryId} was not found. Requested by user {UserId}.",
                command.CategoryId, currentUser.UserId);

            return Result<Guid>.Failure($"Category '{command.CategoryId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Delegate to the aggregate. AddSubCategory:
        //      - throws if the parent Category is deactivated
        //      - throws if a SubCategory with the same name already exists
        //        under this Category (intra-aggregate uniqueness, case-insensitive)
        //      - constructs the SubCategory with the parent's Id
        //      - appends it to the SubCategories collection
        //    EF Core's change tracker will detect the new entity and persist
        //    it on SaveChangesAsync.
        // ------------------------------------------------------------------
        SubCategory subCategory;
        try
        {
            subCategory = category.AddSubCategory(command.Name);
        }
        catch (DomainException ex)
        {
            logger.LogWarning
                ("CreateSubCategory: aggregate rejected creation under category {CategoryId}. Reason: {Reason}. Requested by user {UserId}.",
                command.CategoryId, ex.Message, currentUser.UserId);

            return Result<Guid>.Failure(ex.Message);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("CreateSubCategory: SubCategory {SubCategoryId} ({Name}) created under category {CategoryId} by user {UserId}.",
            subCategory.Id, subCategory.Name, category.Id, currentUser.UserId);

        return Result<Guid>.Success(subCategory.Id);
    }
}