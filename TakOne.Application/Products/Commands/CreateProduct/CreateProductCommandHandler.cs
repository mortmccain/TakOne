using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Products.Entities;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Application.Products.Commands.CreateProduct;

/// <summary>
/// Creates a new Product in the catalog.
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class CreateProductCommandHandler
{
    public static async Task<Result<Guid>> HandleAsync
        (
        CreateProductCommand command,
        ICurrentUserService currentUser,
        IProductRepository productRepository,
        ICategoryRepository categoryRepository,
        IUnitOfWork unitOfWork,
        ILogger<CreateProductCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {

        // ------------------------------------------------------------------
        // 0. Defensive auth check. [RequireRoles] already rejected anonymous
        //    callers via AuthorizationMiddleware, but this handler may also be
        //    invoked from tests or a non-HTTP host — re-checking keeps the
        //    invariant honest.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("CreateProduct: unauthenticated call rejected.");

            return Result<Guid>.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Name uniqueness. Product names are unique across the catalog.
        //    The handler does the friendly check; the DB has a unique index
        //    as a hard guarantee against concurrent requests racing between
        //    our check and our SaveChanges.
        // ------------------------------------------------------------------
        var nameExists = await productRepository.NameExistsAsync(command.Name, excludeId: null, cancellationToken);

        if (nameExists)
        {
            logger.LogWarning
                ("CreateProduct: product name '{Name}' already exists. User {UserId} rejected.",
                command.Name, currentUser.UserId);

            return Result<Guid>.Failure
                ($"A product with the name '{command.Name}' already exists. " + "Choose a different name.");
        }

        // ------------------------------------------------------------------
        // 2. Cross-aggregate category hierarchy validation.
        //    The Product aggregate only checks "SubSub requires Sub" (a
        //    self-contained invariant). It cannot verify that SubCategoryId
        //    belongs to CategoryId, because that requires loading the
        //    Category aggregate. We delegate to the dedicated repository
        //    methods (which the Infrastructure layer implements efficiently
        //    via SQL EXISTS queries — no need to load the whole aggregate).
        // ------------------------------------------------------------------
        var categoryExists = await categoryRepository.ExistsAsync(command.CategoryId, cancellationToken);

        if (!categoryExists)
        {
            logger.LogWarning
                ("CreateProduct: category '{CategoryId}' not found. User {UserId} rejected.", command.CategoryId, currentUser.UserId);

            return Result<Guid>.Failure($"Category '{command.CategoryId}' was not found.");
        }

        if (command.SubCategoryId.HasValue)
        {
            var subBelongsToCategory = await categoryRepository.SubCategoryBelongsToCategoryAsync
                (command.CategoryId, command.SubCategoryId.Value, cancellationToken);

            if (!subBelongsToCategory)
            {
                logger.LogWarning
                    ("CreateProduct: subcategory '{SubCategoryId}' does not belong to category '{CategoryId}'. User {UserId} rejected.",
                    command.SubCategoryId, command.CategoryId, currentUser.UserId);

                return Result<Guid>.Failure
                    ($"SubCategory '{command.SubCategoryId}' does not belong to Category '{command.CategoryId}'.");
            }

            if (command.SubSubCategoryId.HasValue)
            {
                var subSubBelongsToSub = await categoryRepository.SubSubCategoryBelongsToSubCategoryAsync
                     (command.SubCategoryId.Value, command.SubSubCategoryId.Value, cancellationToken);

                if (!subSubBelongsToSub)
                {
                    logger.LogWarning
                        ("CreateProduct: subsubcategory '{SubSubCategoryId}' does not belong tosubcategory '{SubCategoryId}'. User {UserId} rejected.",
                        command.SubSubCategoryId, command.SubCategoryId, currentUser.UserId);

                    return Result<Guid>.Failure
                        ($"SubSubCategory '{command.SubSubCategoryId}' does not belong to SubCategory '{command.SubCategoryId}'.");
                }
            }
        }

        // ------------------------------------------------------------------
        // 3. Construct the domain Money value object from the DTO.
        //    The Money constructor throws DomainException on invalid input
        //    (e.g. wrong-length currency) — caught by middleware and
        //    converted to a Result.Failure.
        // ------------------------------------------------------------------
        var price = new Money(command.Price.Amount, command.Price.Currency);

        // ------------------------------------------------------------------
        // 4. Create the Product via the aggregate's factory method.
        //    Parameter order on Product.Create is:
        //      (name, description, price, stockQuantity, categoryId,
        //       pictureUrl?, subCategoryId?, subSubCategoryId?)
        //    Using named arguments so the call site is self-documenting
        //    and survives future parameter reordering.
        // ------------------------------------------------------------------
        var product = Product.Create
            (
            name: command.Name,
            description: command.Description,
            price: price,
            stockQuantity: command.InitialStockQuantity,
            categoryId: command.CategoryId,
            pictureUrl: command.PictureUrl,
            subCategoryId: command.SubCategoryId,
            subSubCategoryId: command.SubSubCategoryId
            );

        // ------------------------------------------------------------------
        // 5. Persist. EF Core tracks the Product and its owned collection of
        //    CustomerGroupPurchaseLimit value objects as a single unit.
        // ------------------------------------------------------------------
        await productRepository.AddAsync(product, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            (
            "CreateProduct: product {ProductId} ({Name}) created by user {UserId}. Initial stock: {Stock}.",
            product.Id, product.Name, currentUser.UserId, product.StockQuantity
            );

        return Result<Guid>.Success(product.Id);
    }
}