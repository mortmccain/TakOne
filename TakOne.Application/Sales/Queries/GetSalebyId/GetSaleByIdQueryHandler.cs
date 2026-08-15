using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Sales.DTOs;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Sales.Queries.GetSaleById;

/// <summary>
/// Handler for <see cref="GetSaleByIdQuery"/>. See the query file for the
/// authorization model — this class only implements the load + project.
/// </summary>
public sealed class GetSaleByIdQueryHandler
{
    public static async Task<Result<SaleDto>> HandleAsync
        (
        GetSaleByIdQuery query,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        ILogger<GetSaleByIdQueryHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defense-in-depth: even though Wolverine's AuthorizationMiddleware
        //    has already rejected unauthenticated calls, this method may also
        //    be invoked from tests, MediatR-style adapters, or future hosts
        //    that bypass middleware. Re-checking keeps the invariant honest.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("GetSaleById: unauthenticated call rejected.");

            return Result<SaleDto>.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the sale WITH line items — we need them to build the DTO.
        //    Using GetByIdWithLineItemsAsync ensures EF Core eager-loads the
        //    _lineItems collection so we don't hit a lazy-load exception
        //    while projecting.
        // ------------------------------------------------------------------
        var sale = await saleRepository.GetByIdWithLineItemsAsync(query.SaleId, cancellationToken);

        if (sale is null)
        {
            logger.LogInformation
                ("GetSaleById: sale {SaleId} not found. Requested by user {UserId}.",
                query.SaleId, currentUser.UserId);

            return Result<SaleDto>.Failure($"Sale '{query.SaleId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Authorization. Customers may only see their own sales.
        //    Admins, Managers, Employees, and ReadOnly staff can view any
        //    sale (the first three need to for approval, invoicing, and
        //    support; ReadOnly is an audit role whose entire purpose is
        //    to view all sales without modifying them). The check is done
        //    AFTER the load because we need sale.CreatedByUserId to decide.
        //
        //    On failure, return a generic "not found" message — never leak
        //    that the sale exists but the caller can't see it.
        // ------------------------------------------------------------------
        var canViewAnySale =
            currentUser.IsInRole(Roles.Admin) ||
            currentUser.IsInRole(Roles.Manager) ||
            currentUser.IsInRole(Roles.Employee) ||
            currentUser.IsInRole(Roles.ReadOnly);

        if (!canViewAnySale && sale.CreatedByUserId != currentUser.UserId)
        {
            logger.LogWarning
                ("GetSaleById: user {UserId} denied access to sale {SaleId} (created by {OwnerId}).",
                currentUser.UserId, sale.Id, sale.CreatedByUserId);

            return Result<SaleDto>.Failure($"Sale '{query.SaleId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 3. Project to DTO. Audit Guid fields that aren't set yet in the
        //    sale's lifecycle (ApprovedByUserId, InvoicedByUserId,
        //    CancelledByUserId) are projected to null — the domain stores
        //    them as Guid.Empty until set; the DTO hides that convention
        //    from the consumer.
        //
        //    SaleNumber is projected to null when the sale is a Draft (B2
        //    deferred-allocation design). The DTO's DisplayNumber property
        //    computes a pseudo-id (DRAFT-{Guid[0..8]}) when SaleNumber is
        //    null, so the UI always has a display-safe identifier.
        // ------------------------------------------------------------------
        var dto = new SaleDto
        {
            Id = sale.Id,
            SaleNumber = sale.SaleNumber?.Value,
            Status = sale.Status.ToString(),

            CustomerId = sale.CustomerId,
            CustomerName = sale.CustomerName,

            CreatedByUserId = sale.CreatedByUserId,
            CreatedByName = sale.CreatedByName,

            Total = new MoneyDto
            {
                Amount = sale.Total.Amount,
                Currency = sale.Total.Currency
            },

            CreatedAtUtc = sale.CreatedAtUtc,
            SubmittedAtUtc = sale.SubmittedAtUtc,
            ApprovedAtUtc = sale.ApprovedAtUtc,
            InvoicedAtUtc = sale.InvoicedAtUtc,
            CancelledAtUtc = sale.CancelledAtUtc,
            CancellationReason = sale.CancellationReason,

            // Project Guid.Empty → null for audit ids that aren't set yet.
            ApprovedByUserId = sale.ApprovedByUserId == Guid.Empty
                ? null
                : sale.ApprovedByUserId,
            InvoicedByUserId = sale.InvoicedByUserId == Guid.Empty
                ? null
                : sale.InvoicedByUserId,
            CancelledByUserId = sale.CancelledByUserId == Guid.Empty
                ? null
                : sale.CancelledByUserId,

            // Line items are ordered by LineNumber for stable UI rendering.
            // The aggregate assigns LineNumbers sequentially (1, 2, 3, ...)
            // as items are added, so this preserves insertion order.
            //
            // Money field mapping:
            //   - UnitPrice:  straight projection of the snapshot on the line.
            //   - GrossTotal: Quantity × UnitPrice, computed by the domain.
            //   - LineTotal:  TODAY identical to GrossTotal (no discount/tax
            //                 modeling yet). When the domain gains discount
            //                 or tax logic, this projection will change but
            //                 the DTO contract stays stable. See the DTO
            //                 class comment for the full rationale.
            LineItems = sale.LineItems
                .OrderBy(li => li.LineNumber)
                .Select
                (
                li => new SaleLineItemDto
                {
                    Id = li.Id,
                    LineNumber = li.LineNumber,
                    ProductId = li.ProductId,
                    ProductName = li.ProductName,
                    Quantity = li.Quantity,

                    UnitPrice = new MoneyDto
                    {
                        Amount = li.UnitPrice.Amount,
                        Currency = li.UnitPrice.Currency
                    },
                    GrossTotal = new MoneyDto
                    {
                        Amount = li.GrossTotal.Amount,
                        Currency = li.GrossTotal.Currency
                    },
                    LineTotal = new MoneyDto
                    {
                        Amount = li.GrossTotal.Amount,
                        Currency = li.GrossTotal.Currency
                    }
                }
                )
                .ToList()
        };

        return Result<SaleDto>.Success(dto);
    }
}