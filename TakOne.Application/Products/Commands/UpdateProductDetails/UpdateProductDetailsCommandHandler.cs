using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Application.Products.Commands.UpdateProductDetails;

public sealed class UpdateProductDetailsCommandHandler
{
    public static async Task<Result> HandleAsync(
        UpdateProductDetailsCommand command,
        IProductRepository productRepository,
        IUnitOfWork unitOfWork,
        ILogger<UpdateProductDetailsCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        // ------------------------------------------------------------------
        // 1. Load the product. EF Core will track changes, so calling
        //    UpdateDetails on the loaded entity is enough — no explicit
        //    Update() call needed at SaveChanges time.
        // ------------------------------------------------------------------
        var product = await productRepository.GetByIdAsync(command.ProductId, cancellationToken);
        if (product is null)
        {
            return Result.Failure($"Product '{command.ProductId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Name uniqueness — exclude this product's own ID so renaming
        //    to the same name (no-op rename) is allowed. Without the
        //    exclude, every save would fail because the product would
        //    find its own name in the catalog.
        // ------------------------------------------------------------------
        var nameExistsForOther = await productRepository.NameExistsAsync(
            command.Name, excludeId: product.Id, cancellationToken);

        if (nameExistsForOther)
        {
            return Result.Failure(
                $"Another product with the name '{command.Name}' already exists. " +
                "Choose a different name.");
        }

        // ------------------------------------------------------------------
        // 3. Construct the domain Money value object from the DTO.
        // ------------------------------------------------------------------
        var price = new Money(command.Price.Amount, command.Price.Currency);

        // ------------------------------------------------------------------
        // 4. Delegate to the aggregate. UpdateDetails enforces:
        //      - name non-empty + length ≤ 200
        //      - description non-empty + length ≤ 2000
        //      - price.Amount ≥ 0
        //    These are the domain's last-line-of-defense invariants; the
        //    validator already caught friendly-violations earlier.
        //    DomainException is caught by middleware and converted to
        //    Result.Failure.
        // ------------------------------------------------------------------
        product.UpdateDetails(
            name: command.Name,
            description: command.Description,
            price: price,
            pictureUrl: command.PictureUrl);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "UpdateProductDetails: product {ProductId} updated. New name: '{Name}'.",
            product.Id, product.Name);

        return Result.Success();
    }
}