using FluentValidation;

namespace TakOne.Application.Sales.Commands.CreateSale;

/// <summary>
/// CreateSaleCommand has no parameters, so the validator only checks that
/// the current user is authenticated (handled by Wolverine auth middleware,
/// not FluentValidation). This validator exists for consistency with other
/// commands and to act as a placeholder for future pre-validation rules.
/// </summary>
public sealed class CreateSaleCommandValidator : AbstractValidator<CreateSaleCommand>
{
    public CreateSaleCommandValidator()
    {
        // No rules — command has no fields.
    }
}
