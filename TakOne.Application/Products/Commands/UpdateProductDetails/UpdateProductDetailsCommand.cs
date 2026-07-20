using TakOne.Application.Common.Authorization;
using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Products.Commands.UpdateProductDetails;

/// <summary>
/// Updates a Product's basic descriptive fields: name, description, picture,
/// and price. Does NOT touch category or stock — use
/// <see cref="UpdateProductCategoryCommand"/> and
/// <see cref="IncreaseProductStockCommand"/> for those, respectively.
///
/// AUTHORIZATION:
///   Employee, Manager, Admin.
///
/// NAME UNIQUENESS:
///   The handler checks uniqueness via <c>NameExistsAsync(name, excludeId)</c>
///   — the product's own ID is excluded so renaming to the current name is
///   allowed. The DB has a unique index as the hard guarantee.
///
/// PRICE:
///   Passed as <see cref="MoneyDto"/>. The handler constructs the domain
///   <c>Money</c> value object from it.
///
/// PICTURE URL:
///   Nullable. Pass null to remove the picture, pass a URL to set/replace it.
/// </summary>
[RequireRoles(Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed record UpdateProductDetailsCommand(
    Guid ProductId,
    string Name,
    string Description,
    string? PictureUrl,
    MoneyDto Price);