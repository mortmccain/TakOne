using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Products.Commands.UpdateProductCategory;

public sealed class UpdateProductCategoryCommandHandler
{
    public static async Task<Result> HandleAsync(
        UpdateProductCategoryCommand command,
        IProductRepository productRepository,
        ICategoryRepository categoryRepository,
        IUnitOfWork unitOfWork,
        ILogger<UpdateProductCategoryCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        // ------------------------------------------------------------------
        // 1. Load the product. If it doesn't exist, fail fast.
        // ------------------------------------------------------------------
        var product = await productRepository.GetByIdAsync(command.ProductId, cancellationToken);
        if (product is null)
        {
            return Result.Failure($"Product '{command.ProductId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Validate the target category hierarchy. Done BEFORE calling
        //    UpdateCategory so we never leave the aggregate in an
        //    inconsistent in-memory state if a check fails.
        // ------------------------------------------------------------------
        var categoryExists = await categoryRepository.ExistsAsync(command.CategoryId, cancellationToken);
        if (!categoryExists)
        {
            return Result.Failure($"Category '{command.CategoryId}' was not found.");
        }

        if (command.SubCategoryId.HasValue)
        {
            var subBelongsToCategory = await categoryRepository.SubCategoryBelongsToCategoryAsync(
                command.CategoryId, command.SubCategoryId.Value, cancellationToken);

            if (!subBelongsToCategory)
            {
                return Result.Failure(
                    $"SubCategory '{command.SubCategoryId}' does not belong to Category '{command.CategoryId}'.");
            }

            if (command.SubSubCategoryId.HasValue)
            {
                // Argument order: (subCategoryId, subSubCategoryId, ct).
                // Getting it backwards would silently let invalid hierarchies through.
                var subSubBelongsToSub = await categoryRepository.SubSubCategoryBelongsToSubCategoryAsync(
                    command.SubCategoryId.Value, command.SubSubCategoryId.Value, cancellationToken);

                if (!subSubBelongsToSub)
                {
                    return Result.Failure(
                        $"SubSubCategory '{command.SubSubCategoryId}' does not belong to SubCategory '{command.SubCategoryId}'.");
                }
            }
        }

        // ------------------------------------------------------------------
        // 3. Delegate to the aggregate. UpdateCategory enforces:
        //      - categoryId is non-empty
        //      - SubSub requires Sub (self-contained invariant)
        //    DomainException is caught by middleware.
        // ------------------------------------------------------------------
        product.UpdateCategory(
            categoryId: command.CategoryId,
            subCategoryId: command.SubCategoryId,
            subSubCategoryId: command.SubSubCategoryId);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "UpdateProductCategory: product {ProductId} moved to category {CategoryId} " +
            "(sub: {SubId}, subsub: {SubSubId}).",
            product.Id, command.CategoryId, command.SubCategoryId, command.SubSubCategoryId);

        return Result.Success();
    }
}