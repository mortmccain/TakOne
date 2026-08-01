using TakOne.Application.Common.Authorization;
using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Products.Commands.CreateProduct;

/// <summary>
/// Creates a new Product in the catalog.
///
/// AUTHORIZATION:
///   Employee, Manager, Admin. Customers and ReadOnly cannot create products.
///   Role check enforced by <see cref="AuthorizationMiddleware"/> via
///   <see cref="RequireRolesAttribute"/>.
///
/// CATEGORY HIERARCHY:
///   The Product aggregate enforces only that SubSubCategoryId requires
///   SubCategoryId (a self-contained invariant). The CROSS-AGGREGATE invariant
///   — that SubCategoryId actually belongs to CategoryId, and SubSubCategoryId
///   belongs to SubCategoryId — is the handler's responsibility, validated via
///   <see cref="Common.Interfaces.ICategoryRepository"/>.
///
/// PRICE:
///   Passed as <see cref="MoneyDto"/> (Amount + Currency). The handler
///   constructs the domain <c>Money</c> value object from it; the Money
///   constructor enforces that Currency is a 3-letter ISO 4217 code and
///   throws <c>DomainException</c> otherwise (caught by middleware).
///
/// NAME UNIQUENESS:
///   Product names must be unique across the catalog. Enforced by the handler
///   via <see cref="Common.Interfaces.IProductRepository.NameExistsAsync"/>,
///   and backed by a unique index in the database as a hard guarantee against
///   race conditions.
/// </summary>
[RequireRoles(Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed record CreateProductCommand
    (
    string Name,
    string Description,
    string? PictureUrl,
    MoneyDto Price,
    int InitialStockQuantity,
    Guid CategoryId,
    Guid? SubCategoryId,
    Guid? SubSubCategoryId,

    /// <summary>
    /// Per-group purchase limits to attach to the new product. Optional —
    /// pass an empty list (or null) when no limits are needed.
    ///
    /// DDD NOTE: This is a list of <see cref="PurchaseLimitInputDto"/>
    /// (application-layer DTOs), NOT a list of domain
    /// <c>CustomerGroupPurchaseLimit</c> value objects. The handler
    /// converts each DTO into a domain VO via
    /// <c>Product.SetPurchaseLimit(groupName, limit)</c> — the Product
    /// aggregate owns the value-object creation, preserving the DDD
    /// invariant that domain value objects are only ever created by their
    /// parent aggregate.
    /// </summary>
    IReadOnlyList<PurchaseLimitInputDto>? PurchaseLimits = null
    );