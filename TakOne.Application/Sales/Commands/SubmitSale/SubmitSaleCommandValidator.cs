namespace TakOne.Application.Sales.Commands.SubmitSale;

/// <summary>
/// Transitions the current user's Draft Sale to Pending status.
/// After this, the sale is locked for editing and awaits staff approval.
///
/// The submitter is always the sale's creator (CreatedByUserId) — see the
/// domain's Sale.Submit() for the rationale.
/// </summary>
public sealed record SubmitSaleCommand(Guid SaleId);