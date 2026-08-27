using FluentAssertions;
using TakOne.SharedKernel.Common;
using Xunit;

namespace TakOne.SharedKernel.Tests.Common;

/// <summary>
/// Unit tests for <see cref="PaginatedResult{T}"/> — pagination metadata
/// wrapper for paged query results. Verifies the constructor contract and
/// the computed properties TotalPages, HasPreviousPage, HasNextPage.
/// </summary>
public class PaginatedResultTests
{
    [Fact]
    public void Constructor_WhenCalled_SetsAllProperties()
    {
        // Arrange
        var items = new[] { 1, 2, 3 };
        const int totalCount = 30;
        const int pageNumber = 2;
        const int pageSize = 10;

        // Act
        var result = new PaginatedResult<int>(items, totalCount, pageNumber, pageSize);

        // Assert
        result.Items.Should().BeSameAs(items);
        result.TotalCount.Should().Be(totalCount);
        result.PageNumber.Should().Be(pageNumber);
        result.PageSize.Should().Be(pageSize);
    }

    [Fact]
    public void TotalPages_WhenEvenDivision_ReturnsExactQuotient()
    {
        // Arrange: 10 items, page size 5 → 2 pages exactly.
        var result = new PaginatedResult<int>(new[] { 1, 2, 3, 4, 5 }, 10, 1, 5);

        // Act + Assert
        result.TotalPages.Should().Be(2);
    }

    [Fact]
    public void TotalPages_WhenNonEvenDivision_RoundsUp()
    {
        // Arrange: 11 items, page size 5 → Math.Ceiling(11/5.0) = 3.
        var result = new PaginatedResult<int>(new[] { 1, 2, 3 }, 11, 1, 5);

        // Act + Assert
        result.TotalPages.Should().Be(3);
    }

    [Fact]
    public void TotalPages_WhenZeroItemsAndZeroDivision_ReturnsZero()
    {
        // Arrange: TotalCount=0, PageSize=10 → Math.Ceiling(0/10.0) = 0.
        var result = new PaginatedResult<int>(Array.Empty<int>(), 0, 1, 10);

        // Act + Assert
        result.TotalPages.Should().Be(0);
    }

    [Fact]
    public void HasPreviousPage_WhenOnFirstPage_ReturnsFalse()
    {
        // Arrange
        var result = new PaginatedResult<int>(new[] { 1, 2, 3 }, 30, 1, 10);

        // Act + Assert
        result.HasPreviousPage.Should().BeFalse();
    }

    [Fact]
    public void HasPreviousPage_WhenBeyondFirstPage_ReturnsTrue()
    {
        // Arrange
        var result = new PaginatedResult<int>(new[] { 1, 2, 3 }, 30, 2, 10);

        // Act + Assert
        result.HasPreviousPage.Should().BeTrue();
    }

    [Fact]
    public void HasNextPage_WhenOnLastPage_ReturnsFalse()
    {
        // Arrange: 30 items / 10 per page = 3 pages. Page 3 is last.
        var result = new PaginatedResult<int>(new[] { 1, 2, 3 }, 30, 3, 10);

        // Act + Assert
        result.HasNextPage.Should().BeFalse();
    }

    [Fact]
    public void HasNextPage_WhenNotOnLastPage_ReturnsTrue()
    {
        // Arrange: 30 items / 10 per page = 3 pages. Page 2 has a next.
        var result = new PaginatedResult<int>(new[] { 1, 2, 3 }, 30, 2, 10);

        // Act + Assert
        result.HasNextPage.Should().BeTrue();
    }

    [Fact]
    public void SinglePageCase_WhenItemsFitOnePage_HasNoPrevAndNoNext()
    {
        // Arrange: 3 items / 10 per page = 1 page total.
        var result = new PaginatedResult<int>(new[] { 1, 2, 3 }, 3, 1, 10);

        // Act + Assert
        result.TotalPages.Should().Be(1);
        result.HasPreviousPage.Should().BeFalse();
        result.HasNextPage.Should().BeFalse();
    }

    [Fact]
    public void ZeroItemsCase_WhenTotalCountZero_HasNoPrevAndNoNext()
    {
        // Arrange: Empty result on the would-be first page.
        var result = new PaginatedResult<int>(Array.Empty<int>(), 0, 1, 10);

        // Act + Assert
        result.TotalPages.Should().Be(0);
        result.HasPreviousPage.Should().BeFalse();
        result.HasNextPage.Should().BeFalse();
    }

    [Fact]
    public void Items_WhenGivenArray_IsSameReference()
    {
        // Arrange
        var items = new List<int> { 1, 2, 3 }.AsReadOnly();

        // Act
        var result = new PaginatedResult<int>(items, 3, 1, 10);

        // Assert
        result.Items.Should().BeSameAs(items);
    }

    [Fact]
    public void Items_WhenGivenEmptyArray_HasCountZero()
    {
        // Arrange
        var items = Array.Empty<string>();

        // Act
        var result = new PaginatedResult<string>(items, 0, 1, 10);

        // Assert
        result.Items.Should().BeEmpty();
        result.Items.Count.Should().Be(0);
        result.TotalCount.Should().Be(0);
    }

    [Fact]
    public void TotalPages_WhenTotalCountExactlyDivisibleByPageSize_ReturnsExactNumber()
    {
        // Arrange: 25 items / 5 per page = 5 pages exactly.
        var result = new PaginatedResult<int>(new[] { 1 }, 25, 1, 5);

        // Act + Assert
        result.TotalPages.Should().Be(5);
    }

    [Fact]
    public void HasNextPage_OnLastPageOfMultiPageResult_ReturnsFalse()
    {
        // Arrange: 11 items / 5 per page = 3 pages. Page 3 is last (5 + 5 + 1).
        var result = new PaginatedResult<int>(new[] { 1 }, 11, 3, 5);

        // Act + Assert
        result.TotalPages.Should().Be(3);
        result.HasNextPage.Should().BeFalse();
    }
}
