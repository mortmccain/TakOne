using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Categories.Entities;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Categories.Commands.CreateCategory;

/// <summary>
/// Creates a new top-level Category in the catalog.
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class CreateCategoryCommandHandler
{
    public static async Task<Result<Guid>> HandleAsync
        (
        CreateCategoryCommand command,
        ICurrentUserService currentUser,
        ICategoryRepository categoryRepository,
        IUnitOfWork unitOfWork,
        ILogger<CreateCategoryCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check. [RequireRoles] already rejected anonymous
        //    callers via AuthorizationMiddleware, but this handler may also
        //    be invoked from tests or a non-HTTP host — re-checking keeps
        //    the invariant honest.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("CreateCategory: unauthenticated call rejected.");

            return Result<Guid>.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Name uniqueness. Category names are unique across the catalog
        //    (case-insensitive at the DB level — the handler's check here is
        //    a friendly pre-check; the DB unique index is the hard guarantee
        //    against concurrent requests racing between our check and our
        //    SaveChanges).
        // ------------------------------------------------------------------
        var nameExists = await categoryRepository.NameExistsAsync(
            command.Name, excludeId: null, cancellationToken);

        if (nameExists)
        {
            logger.LogWarning
                ("CreateCategory: category name '{Name}' already exists. " + "User {UserId} rejected.",
                command.Name, currentUser.UserId);

            return Result<Guid>.Failure
                ($"A category with the name '{command.Name}' already exists. " + "Choose a different name.");
        }

        // ------------------------------------------------------------------
        // 2. Create the Category via the aggregate's factory method.
        //    Category.Create raises CategoryCreatedDomainEvent, which the
        //    Infrastructure layer's domain-event dispatcher will publish
        //    after SaveChangesAsync succeeds.
        // ------------------------------------------------------------------
        var category = Category.Create(command.Name);

        // ------------------------------------------------------------------
        // 3. Persist. EF Core tracks the Category and its (currently empty)
        //    SubCategories collection as a single unit.
        // ------------------------------------------------------------------
        await categoryRepository.AddAsync(category, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("CreateCategory: category {CategoryId} ({Name}) created by user {UserId}.",
            category.Id, category.Name, currentUser.UserId);

        return Result<Guid>.Success(category.Id);
    }
}