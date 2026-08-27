namespace TakOne.SharedKernel.Primitives;

/// <summary>
/// Base class for all entities (both aggregate roots and value-object-
/// bearing entities). Provides identity equality semantics based on the
/// <see cref="Id"/> field, plus the matching <see cref="GetHashCode"/>
/// override required by the <c>Equals</c> contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>IDENTITY EQUALITY (not reference equality)</b>: two entity instances
/// are considered equal iff they are the exact same runtime type AND their
/// <see cref="Id"/> values are equal (and non-empty). This is the standard
/// DDD entity-equality rule: an entity's identity is its Id, not its
/// location in memory.
/// </para>
/// <para>
/// <b>WHY <c>Id</c> HAS A <c>protected set</c></b>: EF Core needs to set
/// the Id when materializing an entity from the database (the parameterless
/// constructor assigns a fresh Guid, but EF overwrites it with the persisted
/// value). <c>protected</c> (not <c>private</c>) lets derived aggregate
/// roots set the Id via the protected constructor when reconstituting from
/// a known Id (e.g. <c>AggregateRoot(Guid id)</c>). Derived classes should
/// NOT re-declare an <c>Id</c> property — that would hide this one and
/// cause a compiler warning plus silent identity bugs.
/// </para>
/// <para>
/// <b>WHY EMPTY-GUID GUARDS</b>: a transient entity (not yet persisted)
/// has a freshly-assigned random Guid. Two distinct transient entities
/// should never compare equal even if their random Guids happened to
/// collide (astronomically unlikely, but the guard makes the semantics
/// honest). Empty Guid is the marker for "not a real persisted identity".
/// </para>
/// <para>
/// <b>WHY <c>GetHashCode</c> IS OVERRIDDEN</c>: when two objects compare
/// equal via <see cref="Equals(object?)"/>, they MUST produce the same
/// hash code (the .NET contract for <c>object.Equals</c> / <c>GetHashCode</c>).
/// Collections that use hashing (e.g. <c>HashSet&lt;T&gt;</c>,
/// <c>Dictionary&lt;TKey, TValue&gt;</c> keys) rely on this invariant —
/// overriding one without the other causes silent bugs where an entity
/// can't be found in a set/dictionary.
/// </para>
/// </remarks>
public abstract class BaseEntity
{
    /// <summary>
    /// The entity's unique identifier. Assigned a fresh Guid at construction
    /// (transient state); EF Core overwrites this with the persisted value
    /// when materializing from the database.
    /// </summary>
    public Guid Id { get; protected set; }

    /// <summary>
    /// Parameterless constructor for EF Core materialization and transient
    /// entity creation. Assigns a fresh random Guid as the Id.
    /// </summary>
    protected BaseEntity()
    {
        Id = Guid.NewGuid();
    }

    /// <summary>
    /// Constructor for aggregate roots reconstituting from a known Id
    /// (e.g. when the Id is generated upstream or read from a query
    /// parameter before the entity is loaded).
    /// </summary>
    protected BaseEntity(Guid id)
    {
        Id = id;
    }

    /// <summary>
    /// Identity equality: two entities are equal iff they are the exact same
    /// runtime type AND both have non-empty Ids AND those Ids are equal.
    /// </summary>
    public override bool Equals(object? obj)
    {
        // `obj is not BaseEntity other` is a pattern match: it checks the
        // cast AND binds the result to `other` in scope for the rest of the
        // method. If the cast fails, we return false immediately.
        if (obj is not BaseEntity other)
            return false;

        // Same reference → trivially equal (covers comparing an entity to itself).
        if (ReferenceEquals(this, other))
            return true;

        // Two entities are equal only if they are EXACTLY the same type.
        // A Cat with Id=1 is NOT equal to a Dog with Id=1, even if both
        // derive from BaseEntity — they are different concepts.
        if (GetType() != other.GetType())
            return false;

        // Transient entities (not-yet-persisted, random Guid) should never
        // compare equal to anything — even another transient with the same
        // (colliding) Guid. The empty-Guid guard makes the semantics honest.
        if (Id == Guid.Empty || other.Id == Guid.Empty)
            return false;

        return Id == other.Id;
    }

    /// <summary>
    /// Hash code based on <see cref="Id"/>. Required override when
    /// <see cref="Equals(object?)"/> is overridden — equal entities must
    /// produce equal hash codes for hash-based collections to work.
    /// </summary>
    public override int GetHashCode()
    {
        return Id.GetHashCode();
    }

    public static bool operator ==(BaseEntity? left, BaseEntity? right)
    {
        if (left is null && right is null)
            return true;
        if (left is null || right is null)
            return false;
        return left.Equals(right);
    }

    public static bool operator !=(BaseEntity? left, BaseEntity? right)
    {
        return !(left == right);
    }
}
