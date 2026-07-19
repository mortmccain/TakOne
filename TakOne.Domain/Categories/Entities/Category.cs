using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.Primitives;
using TakOne.Domain.Categories.Events;

namespace TakOne.Domain.Categories.Entities;

/// <summary>
/// Aggregate root for the product categorization hierarchy.
///
/// HIERARCHY:
///   Category (aggregate root)
///     └── SubCategory (entity, internal ctor)
///           └── SubSubCategory (entity, internal ctor)
///
/// All three live inside the same aggregate boundary (same transaction),
/// so cross-level invariants — like "you can't add a SubSubCategory to a
/// SubCategory that doesn't belong to this Category" — are enforceable
/// directly inside this class.
///
/// REFERENCES FROM OTHER AGGREGATES:
///   <c>Product.CategoryId</c>, <c>Product.SubCategoryId</c>, and
///   <c>Product.SubSubCategoryId</c> reference the Guid Ids of these entities.
///   They are just Guids — no navigation properties — because Product and
///   Category are different aggregates.
///
/// DEACTIVATION:
///   Soft-delete (IsActive flag). Rows stay so Products keep referencing them.
///   Deactivating a Category also deactivates all its SubCategories and
///   SubSubCategories (cascade at the domain level, not the DB level).
/// </summary>
public sealed class Category : AggregateRoot
{



    // ==================================================================================================================================
    //                                                          PRIVATE FIELDS
    // ==================================================================================================================================



    private readonly List<SubCategory> _subCategories = new();



    // ==================================================================================================================================
    //                                                          PROPERTIES
    // ==================================================================================================================================



    public string Name { get; private set; }

    public bool IsActive { get; private set; }

    public IReadOnlyList<SubCategory> SubCategories
        => _subCategories.AsReadOnly();



    // ==================================================================================================================================
    //                                                          CONSTRUCTORS
    // ==================================================================================================================================



#pragma warning disable CS8618
    /// <summary>
    /// Parameterless constructor required by EF Core. DO NOT use in application code.
    /// </summary>
    private Category() : base(Guid.Empty) { }
#pragma warning restore CS8618

    /// <summary>
    /// Private constructor used by the static factory method.
    /// </summary>
    private Category(string name) : base(Guid.NewGuid())
    {
        EnsureNameValid(name);

        Name = name;
        IsActive = true;
    }



    // ==================================================================================================================================
    //                                                          FACTORY METHOD
    // ==================================================================================================================================



    /// <summary>
    /// Creates a new Category. This is the ONLY way to construct a Category
    /// from application code.
    /// </summary>
    public static Category Create(string name)
    {
        var category = new Category(name);

        category.AddDomainEvent(new CategoryCreatedDomainEvent(category.Id, category.Name));

        return category;
    }



    // ==================================================================================================================================
    //                                                          CATEGORY-LEVEL BEHAVIOR
    // ==================================================================================================================================



    /// <summary>
    /// Renames the Category. Uniqueness across Categories is enforced by the
    /// application layer (query the DB before renaming), not here — DDD rule:
    /// cross-aggregate invariants are checked at the application layer.
    /// </summary>
    public void Rename(string newName)
    {
        EnsureNameValid(newName);
        Name = newName;
    }

    /// <summary>
    /// Deactivates this Category AND all its SubCategories and SubSubCategories.
    /// Cascade is done at the domain level (not via DB cascade) so we keep control.
    /// </summary>
    public void Deactivate()
    {
        IsActive = false;
        foreach (var sub in _subCategories)
        {
            sub.Deactivate();
            foreach (var subSub in sub.SubSubCategories)
            {
                subSub.Deactivate();
            }
        }
    }

    /// <summary>
    /// Reactivates this Category. Does NOT reactivate SubCategories — they must
    /// be reactivated individually, because reactivating a Category shouldn't
    /// silently bring back previously-deactivated children.
    /// </summary>
    public void Activate() => IsActive = true;



    // ==================================================================================================================================
    //                                                          SUBCATEGORY MANAGEMENT
    // ==================================================================================================================================



    /// <summary>
    /// Adds a SubCategory under this Category. The SubCategory's name must be
    /// unique among this Category's SubCategories.
    /// </summary>
    public SubCategory AddSubCategory(string name)
    {
        EnsureActive();
        EnsureSubCategoryNameUnique(name, excludeId: null);

        var sub = new SubCategory(Id, name);
        _subCategories.Add(sub);
        return sub;
    }

    /// <summary>
    /// Renames a SubCategory under this Category.
    /// </summary>
    public void RenameSubCategory(Guid subCategoryId, string newName)
    {
        EnsureActive();
        var sub = EnsureSubCategoryExists(subCategoryId);
        EnsureSubCategoryNameUnique(newName, excludeId: subCategoryId);
        sub.Rename(newName);
    }

    public void DeactivateSubCategory(Guid subCategoryId)
    {
        var sub = EnsureSubCategoryExists(subCategoryId);
        sub.Deactivate();
        // Cascade-deactivate SubSubCategories too.
        foreach (var subSub in sub.SubSubCategories)
        {
            subSub.Deactivate();
        }
    }

    public void ActivateSubCategory(Guid subCategoryId)
    {
        var sub = EnsureSubCategoryExists(subCategoryId);
        sub.Activate();
    }



    // ==================================================================================================================================
    //                                                          SUBSUBCATEGORY MANAGEMENT
    // ==================================================================================================================================



    /// <summary>
    /// Adds a SubSubCategory under a specific SubCategory of this Category.
    /// Routes to the SubCategory's internal method after looking it up.
    /// </summary>
    public SubSubCategory AddSubSubCategory(Guid subCategoryId, string name)
    {
        EnsureActive();
        var sub = EnsureSubCategoryExists(subCategoryId);
        EnsureSubCategoryActive(sub);
        return sub.AddSubSubCategory(name);
    }

    public void RenameSubSubCategory(Guid subCategoryId, Guid subSubCategoryId, string newName)
    {
        EnsureActive();
        var sub = EnsureSubCategoryExists(subCategoryId);
        EnsureSubCategoryActive(sub);
        sub.RenameSubSubCategory(subSubCategoryId, newName);
    }

    public void DeactivateSubSubCategory(Guid subCategoryId, Guid subSubCategoryId)
    {
        var sub = EnsureSubCategoryExists(subCategoryId);
        sub.DeactivateSubSubCategory(subSubCategoryId);
    }

    public void ActivateSubSubCategory(Guid subCategoryId, Guid subSubCategoryId)
    {
        var sub = EnsureSubCategoryExists(subCategoryId);
        sub.ActivateSubSubCategory(subSubCategoryId);
    }



    // ==================================================================================================================================
    //                                                          PRIVATE HELPERS
    // ==================================================================================================================================



    private SubCategory EnsureSubCategoryExists(Guid subCategoryId)
    {
        var sub = _subCategories.FirstOrDefault(s => s.Id == subCategoryId);
        if (sub is null)
            throw new DomainException($"SubCategory with Id '{subCategoryId}' was not found under Category '{Name}'.");

        return sub;
    }

    private void EnsureSubCategoryNameUnique(string name, Guid? excludeId)
    {
        var clash = _subCategories.FirstOrDefault(s =>
            s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            && (excludeId is null || s.Id != excludeId));

        if (clash is not null)
            throw new DomainException($"A SubCategory named '{name}' already exists under Category '{Name}'.");
    }

    private static void EnsureSubCategoryActive(SubCategory sub)
    {
        if (!sub.IsActive)
            throw new DomainException($"Cannot modify SubCategory '{sub.Name}' because it is deactivated.");
    }

    private void EnsureActive()
    {
        if (!IsActive)
            throw new DomainException($"Cannot modify Category '{Name}' because it is deactivated.");
    }



    // ==================================================================================================================================
    //                                                          GUARD METHODS
    // ==================================================================================================================================



    private static void EnsureNameValid(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Category name is required.");

        if (name.Length > 100)
            throw new DomainException("Category name cannot exceed 100 characters.");
    }
}