using System;
using System.Collections.Generic;

namespace TakOne.SharedKernel.Common;

/// <summary>
/// Wraps a paginated collection with metadata for UI pagination controls.
/// </summary>
/// <remarks>
/// The constructor validates <paramref name="pageNumber"/>,
/// <paramref name="pageSize"/> and <paramref name="totalCount"/> so that
/// downstream readers of <see cref="TotalPages"/> cannot trigger a runtime
/// <see cref="OverflowException"/> (the historical behaviour when
/// <see cref="PageSize"/> was 0 caused <c>(int)Math.Ceiling(Infinity)</c>
/// to throw). Callers must pass sane values; the constructor is the
/// single chokepoint that enforces the invariant.
/// </remarks>
public class PaginatedResult<T>
{
    public IReadOnlyList<T> Items { get; }
    public int TotalCount { get; }
    public int PageNumber { get; }
    public int PageSize { get; }

    /// <summary>
    /// Total number of pages in the full result set. Safe even when the
    /// result is empty (returns 0 — no pages, no overflow).
    /// </summary>
    public int TotalPages => PageSize <= 0
        ? 0
        : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasPreviousPage => PageNumber > 1;
    public bool HasNextPage => PageNumber < TotalPages;

    public PaginatedResult(IReadOnlyList<T> items, int totalCount, int pageNumber, int pageSize)
    {
        // Defense-in-depth: validate in the ctor so any future caller
        // (or EF/deserialization path) cannot reach TotalPages with
        // PageSize=0 and crash the host. The ctor is the single source
        // of truth for the invariant.
        if (pageNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), pageNumber, "PageNumber must be >= 1.");
        if (pageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "PageSize must be >= 1.");
        if (totalCount < 0)
            throw new ArgumentOutOfRangeException(nameof(totalCount), totalCount, "TotalCount cannot be negative.");

        Items = items ?? throw new ArgumentNullException(nameof(items));
        TotalCount = totalCount;
        PageNumber = pageNumber;
        PageSize = pageSize;
    }
}
