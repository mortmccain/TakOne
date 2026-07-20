using FluentValidation;
using TakOne.Application.Sales.Commands.CreateSale;

public sealed class CreateSaleCommandValidator : AbstractValidator<CreateSaleCommand>
{
    public CreateSaleCommandValidator()
    {
        RuleFor(x => x.CustomerWorkerId)
            .NotEmpty().WithMessage("Customer worker ID is required.");

        RuleFor(x => x.Items)
            .NotNull().WithMessage("Items list is required.")
            .Must(items => items.Count > 0).WithMessage("At least one item is required to create a sale.");

        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.ProductId).NotEmpty();
            item.RuleFor(i => i.Quantity).GreaterThan(0);
        });
    }
}