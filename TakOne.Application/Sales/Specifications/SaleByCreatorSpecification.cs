using System.Linq.Expressions;
using TakOne.Domain.Sales.Entities;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Specifications;

/// <summary>
/// Specification that selects only <see cref="Sale"/>s created by a given user.
///
/// Used by <c>GetSalesPaginatedQuery</c> when the caller is a customer (or
/// other non-admin role) so they only see their own sales. Admins and managers
/// get an empty specification (see <see cref="Specification{T}.Empty"/>), which
/// resolves to a no-op filter at the database level.
///
/// The "creator" interpretation (rather than "customer") is deliberate: an
/// employee who creates a sale on behalf of a customer is the creator, and
/// the sale should appear on the employee's "sales I started" list — even
/// though it should also appear on the customer's "purchases made for me"
/// list (driven by a separate <c>SaleByCustomerSpecification</c>, if needed).
/// </summary>
public sealed class SaleByCreatorSpecification : Specification<Sale>
{
    private readonly Guid _creatorId;

    public SaleByCreatorSpecification(Guid creatorId)
    {
        // Defensive: a Guid.Empty creator id would silently match every sale
        // whose CreatedByUserId hasn't been set yet (which shouldn't happen,
        // but cheap to guard against).
        if (creatorId == Guid.Empty)
        {
            throw new ArgumentException(
                "Creator id must be a non-empty Guid.", nameof(creatorId));
        }

        _creatorId = creatorId;
    }

    public override Expression<Func<Sale, bool>> ToExpression()
        => sale => sale.CreatedByUserId == _creatorId;
}