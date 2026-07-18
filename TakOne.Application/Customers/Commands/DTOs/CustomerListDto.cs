namespace TakOne.Application.Customers.DTOs;

public sealed class CustomerListDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsActive { get; init; }
}