using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.Primitives;

namespace TakOne.Domain.Categories.Entities;

/// <summary>
/// SubSubCategory entity. Lives inside the Category aggregate boundary (created
/// via <see cref="SubCategory.AddSubSubCategory"/>). Has its own Guid Id so it can be
/// referenced cross-aggregate (e.g. <c>Product.SubSubCategoryId</c>).
///
/// INVARIANTS (enforced by the parent chain Category → SubCategory → SubSubCategory):
///   - Name must be non-empty and unique within its parent SubCategory.
///   - Cannot exist without a parent SubCategory.
///   - Deactivation is soft (IsActive flag); the row stays for audit and for
///     Products that still reference its Id.
/// </summary>
public sealed class SubSubCategory : BaseEntity
{



    // ==================================================================================================================================
    //                                                          PROPERTIES
    // ==================================================================================================================================



    /// <summary>
    /// The Id of the SubCategory this SubSubCategory belongs to.
    /// </summary>
    public Guid SubCategoryId { get; private set; }

    public string Name { get; private set; }

    public bool IsActive { get; private set; }



    // ==================================================================================================================================
    //                                                          CONSTRUCTORS
    // ==================================================================================================================================



#pragma warning disable CS8618
    /// <summary>
    /// Parameterless constructor required by EF Core. DO NOT use in application code.
    /// </summary>
    private SubSubCategory() : base(Guid.Empty) { }
#pragma warning restore CS8618

    /// <summary>
    /// Internal constructor. Only Category (the aggregate root, same assembly)
    /// can create SubSubCategories, via <see cref="SubCategory.AddSubSubCategory"/>.
    /// </summary>
    internal SubSubCategory(Guid subCategoryId, string name) : base(Guid.NewGuid())
    {
        EnsureNameValid(name);

        SubCategoryId = subCategoryId;
        Name = name;
        IsActive = true;
    }



    // ==================================================================================================================================
    //                                                          BEHAVIOR
    // ==================================================================================================================================



    /// <summary>
    /// Renames this SubSubCategory. Uniqueness within the parent SubCategory
    /// is enforced by <see cref="SubCategory.RenameSubSubCategory"/>.
    /// </summary>
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
            throw new DomainException("SubSubCategory name is required.");

        if (name.Length > 100)
            throw new DomainException("SubSubCategory name cannot exceed 100 characters.");
    }
}