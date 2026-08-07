namespace TakOne.Application.Dashboard.DTOs;

/// <summary>
/// One slice in the "Category Distribution" pie chart on the redesigned
/// Dashboard. The chart shows the top 5 categories by NUMBER OF APPROVED
/// SALES that contain products in that category, plus one extra "Others"
/// slice for all remaining categories combined — 6 slices total.
/// </summary>
public sealed class CategorySalesCountDto
{
    /// <summary>
    /// Category name. For the "Others" slice this is the localized "Others"
    /// string (set by the handler using the request culture).
    /// </summary>
    public string CategoryName { get; set; } = string.Empty;

    /// <summary>
    /// Number of approved sales that contain at least one product from this
    /// category. For the "Others" slice, this is the sum of all categories
    /// outside the top 5.
    /// </summary>
    public int SalesCount { get; set; }
}