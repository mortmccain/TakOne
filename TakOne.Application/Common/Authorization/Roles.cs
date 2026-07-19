namespace TakOne.Application.Common.Authorization;

/// <summary>
/// Standard role names used across the application. Keep these in sync with
/// the role names seeded in Infrastructure (step 7).
/// </summary>
public static class Roles
{
    /// <summary>
    /// IT administrator. Can create users and manage everything.
    /// </summary>
    public const string Admin = "Admin";

    /// <summary>
    /// Manager. Can create users, manage products/categories, approve sales.
    /// </summary>
    public const string Manager = "Manager";

    /// <summary>
    /// Sales employee. Can manage products/categories, approve sales, mark as invoiced.
    /// Cannot create users.
    /// </summary>
    public const string Employee = "Employee";

    /// <summary>
    /// Read-only staff. Can view sales, products, categories, but cannot modify.
    /// </summary>
    public const string ReadOnly = "ReadOnly";

    /// <summary>
    /// Customer. Lowest access — can browse the shop and view their own buying history.
    /// Every user (including employees and managers) can also buy — the Customer role
    /// simply denotes the lowest-access staff class.
    /// </summary>
    public const string Customer = "Customer";
}
