namespace TakOne.Application.Dashboard.DTOs;

/// <summary>
/// One row in the "Top Employees by Purchase Amount This Month" card on
/// the redesigned Dashboard. Shows the employees with the highest total
/// purchase amount in the current month.
/// </summary>
public sealed class TopEmployeeDto
{
    /// <summary>
    /// Employee's display name (snapshot from <c>Sale.CustomerName</c>).
    /// </summary>
    public string FullName { get; set; } = string.Empty;

    /// <summary>
    /// Employee's group name (e.g. "مالی", "تولید") — used as the subtitle
    /// under the name. May be empty for staff without a group.
    /// </summary>
    public string GroupName { get; set; } = string.Empty;

    /// <summary>
    /// Total purchase amount this month, already converted to display
    /// currency (Toman when original is IRR).
    /// </summary>
    public decimal TotalAmount { get; set; }

    /// <summary>
    /// Rank (1 = highest). Used by the razor page to render the gold rank
    /// badge on the #1 employee.
    /// </summary>
    public int Rank { get; set; }
}