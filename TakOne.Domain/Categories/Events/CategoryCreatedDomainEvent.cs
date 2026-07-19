using TakOne.SharedKernel.Common;

namespace TakOne.Domain.Categories.Events;

/// <summary>
/// Raised when a new Category is created.
/// </summary>
public sealed class CategoryCreatedDomainEvent : BaseDomainEvent
{
    public Guid CategoryId { get; }
    public string Name { get; }

    public CategoryCreatedDomainEvent(Guid categoryId, string name)
    {
        CategoryId = categoryId;
        Name = name;
    }
}