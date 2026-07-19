namespace TakOne.Application.Common.Authorization;

/// <summary>
/// Marks a command as requiring the authenticated user to be in at least one
/// of the listed ASP.NET Identity roles. Wolverine middleware reads this
/// attribute and rejects the command (returning Result.Failure) if the
/// current user is not in any of the listed roles.
///
/// If the attribute is absent, no role check is performed (any authenticated
/// user may run the command).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class RequireRolesAttribute : Attribute
{
    public string[] Roles { get; }

    public RequireRolesAttribute(params string[] roles)
    {
        if (roles is null || roles.Length == 0)
            throw new ArgumentException("At least one role must be specified.", nameof(roles));

        Roles = roles;
    }
}
