using TakOne.Domain.Sales.Enums;

namespace TakOne.Application.Common.Models;

/// <summary>
/// One row of the daily sale-stats aggregation returned by
/// <c>ISaleRepository.GetDailyStatusStatsAsync</c>: the number of
/// sales and their raw <c>Total.Amount</c> sum, for ONE UTC day and
/// ONE status.
/// </summary>
/// <remarks>
/// <para>
/// The row is keyed by <see cref="Date"/> (a UTC calendar day — the
/// aggregation buckets sales on
/// <c>COALESCE(SubmittedAtUtc, CreatedAtUtc)</c> truncated to the day)
/// and <see cref="Status"/>. One SQL GROUP BY produces all rows for
/// the requested window in a single round-trip.
/// </para>
/// <para>
/// <see cref="TotalAmountRaw"/> is the RAW sum in the sale's original
/// currency (IRR is NOT converted to Toman) — the caller applies the
/// display-currency conversion after the aggregation, matching
/// <c>SumRevenueAsync</c>'s contract.
/// </para>
/// <para>
/// This is a repository return type (direct in-process call), not a
/// Wolverine message — plain positional records are fine.
/// </para>
/// </remarks>
public sealed record DailySaleStatsRow(
    DateTime Date,
    SaleStatus Status,
    int Count,
    decimal TotalAmountRaw);

/// <summary>
/// One row of the per-status sale counts returned by
/// <c>ISaleRepository.GetStatusCountsAsync</c>: how many sales in
/// scope are in each status. Replaces the dashboard's former pattern
/// of one COUNT query per status (plus an in-memory GroupBy for
/// <see cref="SaleStatus.Invoiced"/>) with a single GROUP BY
/// round-trip that returns every status present.
/// </summary>
public sealed record StatusCountRow(
    SaleStatus Status,
    int Count);

/// <summary>
/// One row of the windowed per-status aggregation returned by
/// <c>ISaleRepository.GetWindowStatusStatsAsync</c>: sale count and
/// RAW <c>Total.Amount</c> sum per status, restricted to anchors
/// inside the half-open instant window. The dashboard composes its
/// period-scoped KPIs (orders / amount / approved / invoiced) from
/// the ≤5 rows this returns — instant-precision, so Tehran-midnight
/// window bounds are evaluated exactly.
/// </summary>
public sealed record WindowStatusStatsRow(
    SaleStatus Status,
    int Count,
    decimal TotalAmountRaw);

/// <summary>
/// One row of the top-products aggregation returned by
/// <c>ISaleRepository.GetTopProductsAsync</c>: total quantity and raw
/// revenue-eligible amount summed per <c>SaleLineItem.ProductName</c>
/// across sales anchored in the requested window.
/// </summary>
/// <remarks>
/// The amount is <c>Quantity × UnitPrice.Amount</c> summed per product
/// (the SQL-side equivalent of the domain's computed
/// <c>SaleLineItem.GrossTotal</c> — EF cannot translate the C#
/// property getter, so the arithmetic is expressed inline). RAW
/// currency — no Toman conversion here.
/// </remarks>
public sealed record TopProductSaleRow(
    string ProductName,
    int QuantitySold,
    decimal TotalAmountRaw);

/// <summary>
/// One row of the category sales-count aggregation returned by
/// <c>ISaleRepository.GetCategorySalesCountsAsync</c>: for each
/// product category, the number of DISTINCT revenue-eligible sales
/// whose line items include at least one product of that category.
/// </summary>
/// <remarks>
/// A sale with three line items in the SAME category counts ONCE for
/// that category; a sale with line items in two categories counts once
/// for EACH. This mirrors the dashboard's in-memory
/// per-sale-unique-category logic exactly.
/// </remarks>
public sealed record CategorySaleCountRow(
    Guid CategoryId,
    int SalesCount);

/// <summary>
/// One row of the top-purchasers aggregation returned by
/// <c>ISaleRepository.GetTopPurchasersAsync</c>: total raw
/// <c>Total.Amount</c> per customer across their non-draft,
/// non-cancelled sales anchored in the requested window.
/// </summary>
public sealed record TopPurchaserRow(
    Guid CustomerId,
    string CustomerName,
    decimal TotalAmountRaw);
