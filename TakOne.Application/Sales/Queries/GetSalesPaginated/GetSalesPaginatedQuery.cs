using TakOne.Application.Sales.DTOs;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Queries.GetSalesPaginated;

public sealed class GetSalesPaginatedQuery
{
    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public string? SearchTerm { get; init; }

    /// <summary>
    /// When set, only returns sales created by this user.
    /// Pass null for admins/managers to get all sales.
    /// </summary>
    public Guid? FilterByCreatorId { get; init; }
}