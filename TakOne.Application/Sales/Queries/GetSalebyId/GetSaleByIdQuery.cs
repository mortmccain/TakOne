using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Sales.DTOs;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Sales.Queries.GetSaleById;

/// <summary>
/// Loads a single Sale (with its line items) by Id and projects it to
/// <see cref="SaleDto"/>.
///
/// AUTHORIZATION MODEL:
///   - Admin / Manager / Employee: may view any sale.
///   - Everyone else (customers, read-only): may view only sales they created.
///   This is enforced AFTER the load — we still hit the DB to know who the
///   sale belongs to. If the caller is not allowed, we return Failure with
///   a generic "not found" message rather than a "permission denied" one,
///   to avoid leaking the existence of sales the caller can't see.
///
/// WHY THIS IS A QUERY, NOT A COMMAND:
///   Reads are stateless — no aggregate mutation, no SaveChanges. Wolverine
///   dispatches it through the same pipeline (auth, logging, performance),
///   but no outbox entry is written because nothing changed.
/// </summary>
[RequireAuthentication]
public sealed class GetSaleByIdQuery
{
    public Guid SaleId { get; init; }

    // ----------------------------------------------------------------
    // NOTE: we do NOT pass RequestedByUserId / UserRoles on the query
    // object itself. The handler resolves them from ICurrentUserService,
    // which is the canonical source of "who is calling". Keeping the
    // query object free of identity keeps it serializable for Wolverine's
    // outbox (in case we ever publish it on a bus) and prevents the
    // caller from impersonating another user.
    // ----------------------------------------------------------------
}