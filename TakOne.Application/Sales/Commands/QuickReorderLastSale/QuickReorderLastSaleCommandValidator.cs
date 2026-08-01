using FluentValidation;

namespace TakOne.Application.Sales.Commands.QuickReorderLastSale;

/// <summary>
/// Validator for <see cref="QuickReorderLastSaleCommand"/>.
///
/// The command is a parameter-less record (the handler resolves the caller
/// from <c>ICurrentUserService</c>), so there's nothing to validate at the
/// input boundary. The validator exists for pipeline parity with every
/// other command in the project (Wolverine's validation middleware expects
/// one to exist for every command type) and as a forward-compatibility
/// hook if we ever add parameters (e.g. a specific sale id to repeat,
/// rather than always the most-recent one).
/// </summary>
public sealed class QuickReorderLastSaleCommandValidator : AbstractValidator<QuickReorderLastSaleCommand>
{
    public QuickReorderLastSaleCommandValidator()
    {
        // Intentionally empty — see the class-level XML doc.
    }
}