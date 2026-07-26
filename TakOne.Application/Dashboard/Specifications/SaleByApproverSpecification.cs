using Ardalis.Specification;
using TakOne.Domain.Sales.Entities;

namespace TakOne.Application.Dashboard.Specifications;

/// <summary>
/// Specification that selects only <see cref="Sale"/>s approved by a given
/// user — i.e. <c>Sale.ApprovedByUserId == approverId</c> AND the sale has
/// reached at least <c>SaleStatus.Approved</c>.
///
/// WHY THIS EXISTS:
///   Per roadmap Section 12.2 (Employee dashboard scope — Option D), an
///   Employee's dashboard shows ONLY the sales they personally approved.
///   This is distinct from "sales they created" (which would be
///   <c>SaleByCreatorSpecification</c>) — Employees who create sales on
///   behalf of customers see those on /Sales (their own order history),
///   NOT on the dashboard. The dashboard answers a different question:
///   "what did I do as an approver?"
///
/// WHY THE STATUS FILTER:
///   <c>ApprovedByUserId</c> is set the moment <c>Sale.Approve()</c> is
///   called, which transitions the sale to <c>Approved</c>. The sale then
///   moves on to <c>Invoiced</c> or <c>Cancelled</c>, but
///   <c>ApprovedByUserId</c> is NOT cleared. So filtering on
///   <c>ApprovedByUserId == approverId</c> alone would include sales the
///   employee approved and then later cancelled — which is the right
///   behavior for the dashboard (we want to count cancellations too,
///   they're part of the approver's record). The status filter is therefore
///   <c>Status &gt;= Approved</c> (i.e. Approved, Invoiced, or Cancelled),
///   which excludes Drafts and Pending sales (which have no approver yet
///   anyway — but the filter is defensive in case ApprovedByUserId ever
///   gets set prematurely).
///
/// ARDALIS USAGE:
///   Inherits from <c>Ardalis.Specification.Specification&lt;T&gt;</c>.
///   The <c>Query.Where(...)</c> clause is translated to SQL by the
///   Infrastructure layer's <c>SpecificationEvaluator</c>. The
///   <c>OrderByDescending(CreatedAtUtc)</c> ensures deterministic ordering
///   for any subsequent pagination (not used by the dashboard, but kept
///   for consistency with <c>SaleByCreatorSpecification</c>).
/// </summary>
public sealed class SaleByApproverSpecification : Specification<Sale>
{
    public SaleByApproverSpecification(Guid approverId)
    {
        // Defensive: Guid.Empty would silently match every sale that hasn't
        // been approved yet (the default value of ApprovedByUserId is
        // Guid.Empty until Approve() is called).
        if (approverId == Guid.Empty)
        {
            throw new ArgumentException(
                "Approver id must be a non-empty Guid.", nameof(approverId));
        }

        // Status >= Approved means Approved, Invoiced, or Cancelled.
        // Drafts (1) and Pending (2) are excluded — they have no approver.
        Query.Where(sale =>
            sale.ApprovedByUserId == approverId &&
            sale.Status >= Domain.Sales.Enums.SaleStatus.Approved);

        Query.OrderByDescending(sale => sale.ApprovedAtUtc);
    }
}