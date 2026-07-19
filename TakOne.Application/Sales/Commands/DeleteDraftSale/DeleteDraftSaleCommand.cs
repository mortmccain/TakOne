namespace TakOne.Application.Sales.Commands.DeleteDraftSale;

/// <summary>
/// Hard-deletes the current user's Draft Sale. This is the "discard cart"
/// action — drafts are not kept for audit purposes (only submitted sales are).
///
/// The domain Sale.Cancel() throws for Drafts on purpose — disposal of drafts
/// goes through the repository, not through a state transition.
/// </summary>
public sealed record DeleteDraftSaleCommand(Guid SaleId);