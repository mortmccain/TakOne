using System.Linq.Expressions;
using TakOne.Domain.Sales.Entities;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Specifications;

public sealed class SaleByCreatorSpecification : Specification<Sale>
{
    private readonly Guid _creatorId;

    public SaleByCreatorSpecification(Guid creatorId)
    {
        _creatorId = creatorId;
    }

    public override Expression<Func<Sale, bool>> ToExpression()
        => sale => sale.CreatedByUserId == _creatorId;
}