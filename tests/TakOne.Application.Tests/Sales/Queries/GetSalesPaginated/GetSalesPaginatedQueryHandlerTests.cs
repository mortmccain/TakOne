using System.Linq.Expressions;
using System.Reflection;
using Ardalis.Specification;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Sales.Queries.GetSalesPaginated;
using TakOne.Application.Sales.Specifications;
using TakOne.Domain.Sales.Entities;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Sales.Queries.GetSalesPaginated;

/// <summary>
/// Unit tests for <see cref="GetSalesPaginatedQueryHandler"/>'s Round 3
/// date-range filter wiring.
///
/// COVERAGE APPROACH:
///   The handler's date-range work is: (a) pass the raw UTC bounds into
///   the chosen specification, and (b) let the spec's Where clauses prune
///   rows IN SQL. We capture the <see cref="ISpecification{Sale}"/> handed
///   to the repository mock and EVALUATE it in-memory (via
///   <see cref="SpecificationEvaluator"/>) against sales with known
///   CreatedAtUtc stamps — which verifies both the wiring AND the spec's
///   half-open-interval semantics without a database.
///
/// BOUNDARY SEMANTICS LOCKED IN:
///   [from inclusive, to exclusive) — the canonical date-range interval.
/// </summary>
public class GetSalesPaginatedQueryHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static (
        ICurrentUserService currentUser,
        ISaleRepository saleRepo,
        ILogger<GetSalesPaginatedQueryHandler> logger)
        BuildMocks(bool staff = true)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CustomerId);
        currentUser.IsInRole(Arg.Any<string>()).Returns(false);
        if (staff)
        {
            // Make IsInRole(Admin) the one that returns true — the handler
            // checks Admin/Manager/Employee/ReadOnly; any one suffices.
            currentUser.IsInRole(Roles.Admin).Returns(true);
        }

        var saleRepo = Substitute.For<ISaleRepository>();
        // Default: empty page — individual tests override via When(...).
        saleRepo.GetPaginatedBySpecificationAsync(
                Arg.Any<ISpecification<Sale>>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<Sale>(
                new List<Sale>(), 0, 1, 20));

        var logger = Substitute.For<ILogger<GetSalesPaginatedQueryHandler>>();

        return (currentUser, saleRepo, logger);
    }

    // Sale.CreatedAtUtc is get-only (set to UtcNow in the factory). The
    // date-range tests need sales at KNOWN stamps, so we write the
    // auto-property's compiler-generated backing field directly. Test-only
    // reflection; the domain API stays immutable in production code.
    private static Sale SaleCreatedAt(DateTime utc, Guid? customerId = null)
    {
        var sale = Sale.Create(
            customerId: customerId ?? TestValues.CustomerId,
            customerName: "Alice Customer",
            createdByUserId: TestValues.CreatedByUserId,
            createdByName: "Alice Creator");

        var field = typeof(Sale).GetField(
            "<CreatedAtUtc>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        field!.SetValue(sale, utc);
        return sale;
    }

    /// <summary>
    /// Applies the SPEC's Where expressions to an in-memory sale list —
    /// the same predicates the repository would push to SQL. (Written by
    /// hand instead of SpecificationEvaluator because the EF Core
    /// evaluator lives in a separate NuGet package this test project
    /// doesn't reference; ordering/paging are irrelevant to the filter
    /// semantics under test.)
    /// </summary>
    private static List<Sale> ApplySpec(
        IEnumerable<Sale> source, ISpecification<Sale> spec)
    {
        var query = source.AsQueryable();
        foreach (var where in spec.WhereExpressions)
        {
            query = query.Where(where.Filter);
        }
        return query.ToList();
    }

    /// <summary>
    /// Captures the spec the handler handed to the repository mock, then
    /// applies it in-memory to the given sales.
    /// </summary>
    private static List<Sale> EvaluateCapturedSpec(
        ISaleRepository saleRepo, params Sale[] sales)
    {
        var spec = saleRepo.ReceivedCalls()
            .Select(c => c.GetArguments().FirstOrDefault(a => a is ISpecification<Sale>))
            .Cast<ISpecification<Sale>>()
            .FirstOrDefault();
        spec.Should().NotBeNull("the handler must hand a specification to the repository");

        return ApplySpec(sales, spec!);
    }

    // ── Wiring: the bounds flow into the spec untouched ────────────────

    [Fact]
    public async Task HandleAsync_WithDateRange_PassesBoundsIntoSpec()
    {
        // Arrange — raw UTC instants (e.g. Tehran-local midnights converted
        // by the UI). The handler contract: pass through UNTOUCHED (no
        // flooring, no offset).
        var (currentUser, saleRepo, logger) = BuildMocks();
        var from = new DateTime(2026, 8, 1, 20, 30, 0, DateTimeKind.Utc); // Aug 2 00:00 Tehran
        var to = new DateTime(2026, 8, 10, 20, 30, 0, DateTimeKind.Utc);  // Aug 11 00:00 Tehran

        // Act
        await GetSalesPaginatedQueryHandler.HandleAsync(
            new GetSalesPaginatedQuery
            {
                PageNumber = 1,
                PageSize = 20,
                FromDateUtc = from,
                ToDateUtc = to
            },
            currentUser, saleRepo, logger, CancellationToken.None);

        // Assert — evaluating the captured spec against boundary-stamped
        // sales locks in [from inclusive, to exclusive):
        //   sale at exactly `from`          → IN
        //   sale between                    → IN
        //   sale at exactly `to`            → OUT (exclusive upper)
        //   sale one tick before `from`     → OUT
        var atFrom = SaleCreatedAt(from);
        var between = SaleCreatedAt(from.AddHours(5));
        var atTo = SaleCreatedAt(to);
        var beforeFrom = SaleCreatedAt(from.AddTicks(-1));

        var matched = EvaluateCapturedSpec(saleRepo, atFrom, between, atTo, beforeFrom);

        matched.Should().Contain(atFrom, "the lower bound is INCLUSIVE");
        matched.Should().Contain(between);
        matched.Should().NotContain(atTo, "the upper bound is EXCLUSIVE");
        matched.Should().NotContain(beforeFrom);
    }

    [Fact]
    public async Task HandleAsync_WithOnlyFromDate_FiltersOpenEnded()
    {
        // Arrange — open-ended upper: everything on/after From matches.
        var (currentUser, saleRepo, logger) = BuildMocks();
        var from = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        await GetSalesPaginatedQueryHandler.HandleAsync(
            new GetSalesPaginatedQuery { FromDateUtc = from },
            currentUser, saleRepo, logger, CancellationToken.None);

        var old = SaleCreatedAt(from.AddYears(-1));
        var atFrom = SaleCreatedAt(from);
        var future = SaleCreatedAt(from.AddYears(1));

        var matched = EvaluateCapturedSpec(saleRepo, old, atFrom, future);

        matched.Should().NotContain(old);
        matched.Should().Contain(atFrom);
        matched.Should().Contain(future);
    }

    [Fact]
    public async Task HandleAsync_WithOnlyToDate_FiltersOpenEnded()
    {
        // Arrange — open-ended lower: everything strictly before To matches.
        var (currentUser, saleRepo, logger) = BuildMocks();
        var to = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);

        await GetSalesPaginatedQueryHandler.HandleAsync(
            new GetSalesPaginatedQuery { ToDateUtc = to },
            currentUser, saleRepo, logger, CancellationToken.None);

        var ancient = SaleCreatedAt(to.AddYears(-5));
        var justBefore = SaleCreatedAt(to.AddTicks(-1));
        var atTo = SaleCreatedAt(to);

        var matched = EvaluateCapturedSpec(saleRepo, ancient, justBefore, atTo);

        matched.Should().Contain(ancient);
        matched.Should().Contain(justBefore);
        matched.Should().NotContain(atTo, "the upper bound is EXCLUSIVE");
    }

    [Fact]
    public async Task HandleAsync_WithNoBounds_MatchesEverything()
    {
        // Backward compatibility: a query without bounds must behave
        // exactly as before — no date Where clauses at all.
        var (currentUser, saleRepo, logger) = BuildMocks();

        await GetSalesPaginatedQueryHandler.HandleAsync(
            new GetSalesPaginatedQuery(),
            currentUser, saleRepo, logger, CancellationToken.None);

        var s1 = SaleCreatedAt(DateTime.UtcNow.AddYears(-3));
        var s2 = SaleCreatedAt(DateTime.UtcNow);

        var matched = EvaluateCapturedSpec(saleRepo, s1, s2);
        matched.Should().HaveCount(2);
    }

    // ── Degenerate range: empty result, not an error ───────────────────

    [Fact]
    public async Task HandleAsync_WithInvertedRange_ReturnsEmptyPage()
    {
        // From AFTER To is degenerate — half-open [from, to) with from ≥ to
        // matches nothing. The handler's lenient contract (same as an
        // over-specific search term): empty result, NOT a failure.
        var (currentUser, saleRepo, logger) = BuildMocks();
        var from = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = await GetSalesPaginatedQueryHandler.HandleAsync(
            new GetSalesPaginatedQuery { FromDateUtc = from, ToDateUtc = to },
            currentUser, saleRepo, logger, CancellationToken.None);

        // PaginatedResult is a plain paged wrapper (no IsSuccess) — the
        // handler's lenient contract surfaces a degenerate range as an
        // EMPTY page, never as an exception.
        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    // ── Customer scoping still applies with a date range ───────────────

    [Fact]
    public async Task HandleAsync_CustomerWithDateRange_UsesCustomerScopedSpec()
    {
        // The date range must not accidentally widen customer scoping: a
        // non-staff caller gets the SaleByCustomerSpecification (own sales
        // only) WITH the date bounds folded in.
        var (currentUser, saleRepo, logger) = BuildMocks(staff: false);
        var from = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        await GetSalesPaginatedQueryHandler.HandleAsync(
            new GetSalesPaginatedQuery { FromDateUtc = from },
            currentUser, saleRepo, logger, CancellationToken.None);

        var spec = saleRepo.ReceivedCalls()
            .Select(c => c.GetArguments().FirstOrDefault(a => a is ISpecification<Sale>))
            .Cast<ISpecification<Sale>>()
            .First();
        spec.Should().BeOfType<SaleByCustomerSpecification>();

        // A sale for ANOTHER customer created inside the range must not
        // leak into the customer's list even though it matches the dates.
        var otherCustomerSale = SaleCreatedAt(
            from.AddHours(1), customerId: Guid.NewGuid());
        var matched = ApplySpec(new[] { otherCustomerSale }, spec);

        matched.Should().BeEmpty("the date filter must not bypass the customer scope");
    }

    // ── Spec-only tests (direct construction, no handler) ──────────────

    [Fact]
    public void AllSalesSpecification_WithDateRange_FiltersInMemory()
    {
        // Direct spec test: verifies the Where clauses independent of the
        // handler (belt + braces with the captured-spec tests above).
        var from = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc);
        var spec = new AllSalesSpecification(status: null, fromUtc: from, toUtcExclusive: to);

        var before = SaleCreatedAt(from.AddDays(-1));
        var atFrom = SaleCreatedAt(from);
        var middle = SaleCreatedAt(from.AddDays(5));
        var atTo = SaleCreatedAt(to);

        var matched = ApplySpec(new[] { before, atFrom, middle, atTo }, spec);

        matched.Should().BeEquivalentTo(new[] { atFrom, middle });
    }

    [Fact]
    public void SaleByCustomerSpecification_WithDateRange_FiltersInMemory()
    {
        var from = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc);
        var spec = new SaleByCustomerSpecification(
            TestValues.CustomerId, status: null, fromUtc: from, toUtcExclusive: to);

        var inRangeOwn = SaleCreatedAt(from.AddDays(1));
        var outOfRangeOwn = SaleCreatedAt(from.AddDays(-1));

        var matched = ApplySpec(new[] { inRangeOwn, outOfRangeOwn }, spec);

        matched.Should().Contain(inRangeOwn);
        matched.Should().NotContain(outOfRangeOwn);
    }

    [Fact]
    public void Specifications_OlderOverloads_StillConstructWithoutBounds()
    {
        // Backward compatibility of the ctor chain: existing callers (and
        // tests) using the pre-Round-3 constructors keep compiling and
        // produce specs with no date clauses.
        var _ = new AllSalesSpecification();
        var __ = new AllSalesSpecification(status: null);
        var ___ = new SaleByCustomerSpecification(TestValues.CustomerId);
        var ____ = new SaleByCustomerSpecification(TestValues.CustomerId, status: null);
    }
}
