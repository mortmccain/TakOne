using System;
using System.Collections.Generic;
using System.Linq;

namespace TakOne.SharedKernel.Primitives;

/// <summary>
/// Base class for DDD value objects. Implementations override
/// <see cref="GetEqualityComponents"/> to declare the atomic components
/// that participate in equality and hashing.
/// </summary>
/// <remarks>
/// <para>
/// <b>HASHING STRATEGY:</b> Uses <see cref="HashCode"/> (the .NET
/// core library hash combiner) instead of the historical XOR-fold.
/// The XOR-fold had two correctness defects:
/// <list type="bullet">
/// <item><c>XOR(a, a) == 0</c> — two identical components cancelled
/// each other out, producing the same hash as an empty value object.</item>
/// <item><c>Aggregate((x,y) => x^y)</c> threw <see cref="InvalidOperationException"/>
/// on an empty component sequence.</item>
/// </list>
/// <see cref="HashCode"/> uses a better mixing function (FNV-style with
/// prime multipliers) and gracefully handles empty sequences and null
/// components.
/// </para>
/// </remarks>
public abstract class BaseValueObject
{
    // Each derived class must tell us which properties to compare.
    protected abstract IEnumerable<object> GetEqualityComponents();

    public override bool Equals(object? obj)
    {
        if (obj == null || obj.GetType() != GetType())
            return false;
        var other = (BaseValueObject)obj;
        var thisComponents = GetEqualityComponents();
        var otherComponents = other.GetEqualityComponents();

        return thisComponents.SequenceEqual(otherComponents);
    }

    public override int GetHashCode()
    {
        // Use HashCode (modern, well-mixed combiner) — NOT the XOR-fold.
        // The XOR-fold had two defects:
        //   (a) XOR(a, a) == 0 — two identical components cancel,
        //       producing collisions (e.g. a VO with components [X, X]
        //       hashed to the same value as an empty VO);
        //   (b) Aggregate on an empty sequence threw InvalidOperationException.
        // HashCode handles both edge cases correctly.
        var hash = new HashCode();
        foreach (var component in GetEqualityComponents())
        {
            hash.Add(component);
        }
        return hash.ToHashCode();
    }

    public static bool operator ==(BaseValueObject? left, BaseValueObject? right)
    {
        if (left is null && right is null) return true;
        if (left is null || right is null) return false;
        return left.Equals(right);
    }

    public static bool operator !=(BaseValueObject? left, BaseValueObject? right)
    {
        return !(left == right);
    }
}
