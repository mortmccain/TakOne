using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.Primitives;

namespace TakOne.Domain.Categories.Entities;

/// <summary>
/// SubCategory entity. Lives inside the Category aggregate boundary (created
/// via <see cref="Category.AddSubCategory"/>). Has its own Guid Id so it can be
/// referenced cross-aggregate (e.g. <c>Product.SubCategoryId</c>).
///
/// Owns a collection of <see cref="SubSubCategory"/> entities, and enforces
/// uniqueness of their names within itself.
/// </summary>
public sealed class SubCategory : BaseEntity
{



    // ==================================================================================================================================
    //                                                          PRIVATE FIELDS
    // ==================================================================================================================================



    private readonly List<SubSubCategory> _subSubCategories = new();



    // ==================================================================================================================================
    //                                                          PROPERTIES
    // ==================================================================================================================================



    /// <summary>
    /// The Id of the Category this SubCategory belongs to.
    /// </summary>
    public Guid CategoryId { get; private set; }

    public string Name { get; private set; }

    public bool IsActive { get; private set; }

    public IReadOnlyList<SubSubCategory> SubSubCategories
        => _subSubCategories.AsReadOnly();



    // ==================================================================================================================================
    //                                                          CONSTRUCTORS
    // ==================================================================================================================================



#pragma warning disable CS8618
    /// <summary>
    /// Parameterless constructor required by EF Core. DO NOT use in application code.
    /// </summary>
    private SubCategory() : base(Guid.Empty) { }
#pragma warning restore CS8618

    /// <summary>
    /// Internal constructor. Only Category (the aggregate root, same assembly)
    /// can create SubCategories, via <see cref="Category.AddSubCategory"/>.
    /// </summary>
    internal SubCategory(Guid categoryId, string name) : base(Guid.NewGuid())
    {
        EnsureNameValid(name);

        CategoryId = categoryId;
        Name = name;
        IsActive = true;
    }



    // ==================================================================================================================================
    //                                                          SUBSUBCATEGORY MANAGEMENT
    // ==================================================================================================================================



    /// <summary>
    /// Adds a SubSubCategory under this SubCategory. Internal — exposed
    /// publicly via <see cref="Category.AddSubSubCategory"/>, which routes
    /// the call here after looking up the parent SubCategory.
    /// </summary>
    internal SubSubCategory AddSubSubCategory(string name)
    {
        EnsureSubSubCategoryNameUnique(name, excludeId: null);

        var subSub = new SubSubCategory(Id, name);
        _subSubCategories.Add(subSub);
        return subSub;
    }

    /// <summary>
    /// Renames a SubSubCategory under this SubCategory.
    /// Internal — exposed publicly via <see cref="Category.RenameSubSubCategory"/>.
    /// </summary>
    internal void RenameSubSubCategory(Guid subSubCategoryId, string newName)
    {
        var subSub = EnsureSubSubCategoryExists(subSubCategoryId);
        EnsureSubSubCategoryNameUnique(newName, excludeId: subSubCategoryId);
        subSub.Rename(newName);
    }

    internal void DeactivateSubSubCategory(Guid subSubCategoryId)
    {
        var subSub = EnsureSubSubCategoryExists(subSubCategoryId);
        subSub.Deactivate();
    }

    internal void ActivateSubSubCategory(Guid subSubCategoryId)
    {
        var subSub = EnsureSubSubCategoryExists(subSubCategoryId);
        subSub.Activate();
    }



    // ==================================================================================================================================
    //                                                          PRIVATE HELPERS
    // ==================================================================================================================================



    private SubSubCategory EnsureSubSubCategoryExists(Guid subSubCategoryId)
    {
        var subSub = _subSubCategories.FirstOrDefault(s => s.Id == subSubCategoryId);
        if (subSub is null)
            throw new DomainException($"SubSubCategory with Id '{subSubCategoryId}' was not found under SubCategory '{Name}'.");

        return subSub;
    }

    private void EnsureSubSubCategoryNameUnique(string name, Guid? excludeId)
    {
        var clash = _subSubCategories.FirstOrDefault(s =>
            s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            && (excludeId is null || s.Id != excludeId));

        if (clash is not null)
            throw new DomainException($"A SubSubCategory named '{name}' already exists under SubCategory '{Name}'.");
    }



    // ==================================================================================================================================
    //                                                          BEHAVIOR
    // ==================================================================================================================================



    internal void Rename(string newName)
    {
        EnsureNameValid(newName);
        Name = newName;
    }

    internal void Deactivate() => IsActive = false;
    internal void Activate() => IsActive = true;



    // ==================================================================================================================================
    //                                                          GUARD METHODS
    // ==================================================================================================================================



    private static void EnsureNameValid(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("SubCategory name is required.");

        if (name.Length > 100)
            throw new DomainException("SubCategory name cannot exceed 100 characters.");
    }
}