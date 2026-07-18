namespace TakOne.Application.Customers.Commands.CreateCustomer;

/// <summary>
/// Command to create a new Customer.
/// Returns the ID of the newly created Customer on success.
/// </summary>
public sealed class CreateCustomerCommand
{
    public string Name { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string PersonalId { get; init; } = string.Empty;
    public bool IsActive { get; init; } = true;
    
}