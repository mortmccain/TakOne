using System.Linq.Expressions;

namespace TakOne.SharedKernel.Common;

/// <summary>
/// Base class for the Specification pattern.
/// Encapsulates query logic into composable, reusable components.
/// </summary>
public abstract class Specification<T>
{
    public static readonly Specification<T> Empty = new EmptySpecification<T>();

    public abstract Expression<Func<T, bool>> ToExpression();

    public bool IsSatisfiedBy(T entity)
    {
        /*
         ToExpression() → Gets the recipe.
         .Compile() → Turns the recipe into real executable code (turns paper into cooked food).
         .Invoke(entity) → Runs that code on the object you passed in.
         */
        return ToExpression().Compile().Invoke(entity);
    }

    public Specification<T> And(Specification<T> other)
    {
        return new AndSpecification<T>(this, other);
    }

    public Specification<T> Or(Specification<T> other)
    {
        return new OrSpecification<T>(this, other);
    }

    public Specification<T> Not()
    {
        return new NotSpecification<T>(this);
    }
}

internal sealed class EmptySpecification<T> : Specification<T>
{
    public override Expression<Func<T, bool>> ToExpression()
    {
        return _ => true;
    }
}

internal sealed class AndSpecification<T> : Specification<T>
{
    private readonly Specification<T> _left;
    private readonly Specification<T> _right;

    public AndSpecification(Specification<T> left, Specification<T> right)
    {
        _left = left;
        _right = right;
    }

    public override Expression<Func<T, bool>> ToExpression()
    {
        var leftExpr = _left.ToExpression();
        var rightExpr = _right.ToExpression();
        var parameter = Expression.Parameter(typeof(T));
        var combined = Expression.AndAlso
            (
            Expression.Invoke(leftExpr, parameter),
            Expression.Invoke(rightExpr, parameter)
            );
        return Expression.Lambda<Func<T, bool>>(combined, parameter);
    }
}

internal sealed class OrSpecification<T> : Specification<T>
{
    private readonly Specification<T> _left;
    private readonly Specification<T> _right;

    public OrSpecification(Specification<T> left, Specification<T> right)
    {
        _left = left;
        _right = right;
    }

    public override Expression<Func<T, bool>> ToExpression()
    {
        var leftExpr = _left.ToExpression();
        var rightExpr = _right.ToExpression();
        var parameter = Expression.Parameter(typeof(T));
        var combined = Expression.OrElse
            (
            Expression.Invoke(leftExpr, parameter),
            Expression.Invoke(rightExpr, parameter)
            );
        return Expression.Lambda<Func<T, bool>>(combined, parameter);
    }
}

internal sealed class NotSpecification<T> : Specification<T>
{
    private readonly Specification<T> _specification;

    public NotSpecification(Specification<T> specification)
    {
        _specification = specification;
    }

    public override Expression<Func<T, bool>> ToExpression()
    {
        var expr = _specification.ToExpression();
        var parameter = Expression.Parameter(typeof(T));
        var notExpression = Expression.Not(Expression.Invoke(expr, parameter));
        return Expression.Lambda<Func<T, bool>>(notExpression, parameter);
    }
}