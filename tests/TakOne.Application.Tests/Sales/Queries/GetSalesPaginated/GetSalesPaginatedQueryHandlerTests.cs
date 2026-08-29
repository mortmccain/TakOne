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

    // ── Round 4: server-driven paging wiring ───────────────────────────

    /// <summary>
    /// Builds a sale with a KNOWN sale number + total + names, via the
    /// same test-only reflection the CreatedAtUtc helper uses (the domain
    /// API stays immutable in production code).
    /// </summary>
    private static Sale SaleWith(
        int? saleYear = null,
        int? saleSequence = null,
        decimal totalAmount = 0m,
        string customerName = "Alice Customer",
        string createdByName = "Alice Creator",
        DateTime? createdAt = null,
        Guid? customerId = null)
    {
        var sale = Sale.Create(
            customerId: customerId ?? TestValues.CustomerId,
            customerName: customerName,
            createdByUserId: TestValues.CreatedByUserId,
            createdByName: createdByName);

        if (saleYear.HasValue && saleSequence.HasValue)
        {
            typeof(Sale).GetProperty(nameof(Sale.SaleNumber))!
                .SetValue(sale, Domain.Sales.ValueObjects.SaleNumber.Create(saleYear.Value, saleSequence.Value));
        }

        typeof(Sale).GetProperty(nameof(Sale.Total))!
            .SetValue(sale, new TakOne.SharedKernel.ValueObjects.Money(totalAmount, "IRR"));

        if (createdAt.HasValue)
        {
            typeof(Sale).GetField("<CreatedAtUtc>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(sale, createdAt.Value);
        }

        return sale;
    }

    /// <summary>
    /// Human-readable "path" of an order expression's key selector
    /// (e.g. "Total.Amount" / "SaleNumber.Year") after unwrapping the
    /// object-conversion node Ardalis wraps around it.
    /// </summary>
    private static string KeyPath<T>(System.Linq.Expressions.Expression<Func<T, object?>> expr)
    {
        var body = expr.Body;
        while (body is System.Linq.Expressions.UnaryExpression { NodeType: System.Linq.Expressions.ExpressionType.Convert } convert)
        {
            body = convert.Operand;
        }

        var parts = new List<string>();
        while (body is System.Linq.Expressions.MemberExpression member)
        {
            parts.Add(member.Member.Name);
            body = member.Expression!;
        }

        parts.Reverse();
        return string.Join(".", parts);
    }

    [Fact]
    public async Task HandleAsync_DefaultSort_IsNewestFirstWithIdTiebreaker()
    {
        // No user sort active → the spec must default to CreatedAtUtc
        // DESC with an Id tiebreaker (deterministic OFFSET/FETCH paging).
        var (currentUser, saleRepo, logger) = BuildMocks();

        await GetSalesPaginatedQueryHandler.HandleAsync(
            new GetSalesPaginatedQuery(),
            currentUser, saleRepo, logger, CancellationToken.None);

        var spec = CaptureSpec(saleRepo);
        var orderPaths = spec.OrderExpressions
            .Select(o => (o.OrderType, Path: KeyPath(o.KeySelector)))
            .ToList();

        orderPaths.Should().Contain(
            (OrderTypeEnum.OrderByDescending, "CreatedAtUtc"),
            "the default sort is newest-first");
        orderPaths.Should().Contain(
            (OrderTypeEnum.ThenByDescending, "Id"),
            "the Id tiebreaker keeps paging deterministic");
        // No ascending clause anywhere — the default is fully descending.
        orderPaths.Should().NotContain(o => o.OrderType == OrderTypeEnum.OrderBy);
    }

    [Fact]
    public async Task HandleAsync_WithSort_PassesSortIntoSpec()
    {
        // A user sort on the Total column (ascending) must arrive in the
        // spec as OrderBy(Total.Amount) + ThenBy(Id).
        var (currentUser, saleRepo, logger) = BuildMocks();

        await GetSalesPaginatedQueryHandler.HandleAsync(
            new GetSalesPaginatedQuery
            {
                SortBy = SalesSortBy.Total,
                SortDescending = false
            },
            currentUser, saleRepo, logger, CancellationToken.None);

        var spec = CaptureSpec(saleRepo);
        var orderPaths = spec.OrderExpressions
            .Select(o => (o.OrderType, Path: KeyPath(o.KeySelector)))
            .ToList();

        orderPaths.Should().Contain((OrderTypeEnum.OrderBy, "Total.Amount"));
        orderPaths.Should().Contain((OrderTypeEnum.ThenBy, "Id"));
    }

    [Fact]
    public async Task HandleAsync_CustomerWithColumnFilters_GetsSameFiltersInScopedSpec()
    {
        // Column filters must NOT widen customer scoping: a non-staff
        // caller gets the SaleByCustomerSpecification WITH the same
        // filter clauses folded in.
        var (currentUser, saleRepo, logger) = BuildMocks(staff: false);

        await GetSalesPaginatedQueryHandler.HandleAsync(
            new GetSalesPaginatedQuery
            {
                CustomerNameFilter = new SalesTextFilter("bob", SalesTextOperator.Contains)
            },
            currentUser, saleRepo, logger, CancellationToken.None);

        var spec = CaptureSpec(saleRepo);
        spec.Should().BeOfType<SaleByCustomerSpecification>();

        var ownBobSale = SaleWith(customerName: "Bob Builder");
        var otherCustomerSale = SaleWith(customerName: "Bob Stranger", customerId: Guid.NewGuid());
        var ownNonBobSale = SaleWith(customerName: "Alice Customer");

        var matched = ApplySpec(new[] { ownBobSale, otherCustomerSale, ownNonBobSale }, spec);

        matched.Should().Contain(ownBobSale, "the customer-name filter applies inside the customer scope");
        matched.Should().NotContain(otherCustomerSale, "the customer scope is not bypassed by column filters");
        matched.Should().NotContain(ownNonBobSale, "the customer-name filter prunes non-matching names");
    }

    [Fact]
    public async Task HandleAsync_WithSearchTerm_ServerSideNameOrNumberMatch()
    {
        // The legacy SearchTerm (MobileSearch) is now a server-side OR:
        // customer-name contains OR number-shape match. It must match
        // rows ACROSS the whole result set (the pre-Round-4 in-memory
        // version only filtered the loaded page).
        var (currentUser, saleRepo, logger) = BuildMocks();

        await GetSalesPaginatedQueryHandler.HandleAsync(
            new GetSalesPaginatedQuery { SearchTerm = "bob" },
            currentUser, saleRepo, logger, CancellationToken.None);

        var spec = CaptureSpec(saleRepo);

        var bob = SaleWith(customerName: "Bob Builder");
        var named = SaleWith(saleYear: 1405, saleSequence: 42, customerName: "Alice");
        var draft = SaleWith(customerName: "Drafty McDraftface");
        var other = SaleWith(customerName: "Alice Customer");

        var matched = ApplySpec(new[] { bob, named, draft, other }, spec);

        matched.Should().Contain(bob, "search matches the customer name (case-insensitive)");
        matched.Should().NotContain(named, "a name search does not match unrelated numbered sales");
        matched.Should().NotContain(draft);
        matched.Should().NotContain(other);
    }

    [Fact]
    public async Task HandleAsync_WithSearchTerm_FullNumber_MatchesExactSale()
    {
        var (currentUser, saleRepo, logger) = BuildMocks();

        await GetSalesPaginatedQueryHandler.HandleAsync(
            new GetSalesPaginatedQuery { SearchTerm = "INT-1405-42" },
            currentUser, saleRepo, logger, CancellationToken.None);

        var spec = CaptureSpec(saleRepo);

        var target = SaleWith(saleYear: 1405, saleSequence: 42);
        var sameYearOtherSeq = SaleWith(saleYear: 1405, saleSequence: 43);
        var otherYear = SaleWith(saleYear: 1406, saleSequence: 42);
        var nameOnly = SaleWith(customerName: "int-1405-42 fan");

        var matched = ApplySpec(new[] { target, sameYearOtherSeq, otherYear, nameOnly }, spec);

        matched.Should().Contain(target, "the full number matches its exact (year, sequence)");
        matched.Should().NotContain(sameYearOtherSeq);
        matched.Should().NotContain(otherYear);
        matched.Should().Contain(nameOnly, "the customer-name OR arm still matches");
    }

    [Fact]
    public async Task HandleAsync_WithSaleNumberTerm_DraftsOnly()
    {
        var (currentUser, saleRepo, logger) = BuildMocks();

        await GetSalesPaginatedQueryHandler.HandleAsync(
            new GetSalesPaginatedQuery { SaleNumberTerm = "draft" },
            currentUser, saleRepo, logger, CancellationToken.None);

        var spec = CaptureSpec(saleRepo);

        var draft = SaleWith();
        var numbered = SaleWith(saleYear: 1405, saleSequence: 1);

        var matched = ApplySpec(new[] { draft, numbered }, spec);

        matched.Should().Contain(draft, "'draft' matches drafts (NULL SaleNumber)");
        matched.Should().NotContain(numbered);
    }

    [Fact]
    public async Task HandleAsync_WithSaleNumberTerm_Garbage_MatchesNothing()
    {
        var (currentUser, saleRepo, logger) = BuildMocks();

        await GetSalesPaginatedQueryHandler.HandleAsync(
            new GetSalesPaginatedQuery { SaleNumberTerm = "zzz-not-a-number" },
            currentUser, saleRepo, logger, CancellationToken.None);

        var spec = CaptureSpec(saleRepo);

        var anySale = SaleWith(saleYear: 1405, saleSequence: 1);
        ApplySpec(new[] { anySale }, spec).Should().BeEmpty(
            "an unparseable term must not silently widen to all rows");
    }

    [Fact]
    public async Task HandleAsync_WithTotalFilter_AppliesComparison()
    {
        var (currentUser, saleRepo, logger) = BuildMocks();

        await GetSalesPaginatedQueryHandler.HandleAsync(
            new GetSalesPaginatedQuery
            {
                TotalFilter = new SalesAmountFilter(SalesAmountOperator.GreaterThan, 100m)
            },
            currentUser, saleRepo, logger, CancellationToken.None);

        var spec = CaptureSpec(saleRepo);

        var cheap = SaleWith(totalAmount: 50m);
        var atBoundary = SaleWith(totalAmount: 100m);
        var expensive = SaleWith(totalAmount: 150m);

        var matched = ApplySpec(new[] { cheap, atBoundary, expensive }, spec);

        matched.Should().Contain(expensive);
        matched.Should().NotContain(atBoundary, "GreaterThan is strict");
        matched.Should().NotContain(cheap);
    }

    [Fact]
    public async Task HandleAsync_WithTextFilterOperators_NotContains()
    {
        var (currentUser, saleRepo, logger) = BuildMocks();

        await GetSalesPaginatedQueryHandler.HandleAsync(
            new GetSalesPaginatedQuery
            {
                CustomerNameFilter = new SalesTextFilter("bob", SalesTextOperator.NotContains)
            },
            currentUser, saleRepo, logger, CancellationToken.None);

        var spec = CaptureSpec(saleRepo);

        var bob = SaleWith(customerName: "Bob Builder");
        var alice = SaleWith(customerName: "Alice Customer");

        var matched = ApplySpec(new[] { bob, alice }, spec);

        matched.Should().NotContain(bob);
        matched.Should().Contain(alice);
    }

    [Fact]
    public async Task HandleAsync_WithCreatedByNameFilter_AppliesTextFilter()
    {
        var (currentUser, saleRepo, logger) = BuildMocks();

        await GetSalesPaginatedQueryHandler.HandleAsync(
            new GetSalesPaginatedQuery
            {
                CreatedByNameFilter = new SalesTextFilter("creator", SalesTextOperator.Contains)
            },
            currentUser, saleRepo, logger, CancellationToken.None);

        var spec = CaptureSpec(saleRepo);

        var byCreator = SaleWith(createdByName: "Alice Creator");
        var byOther = SaleWith(createdByName: "Bob Staff");

        var matched = ApplySpec(new[] { byCreator, byOther }, spec);

        matched.Should().Contain(byCreator);
        matched.Should().NotContain(byOther);
    }

    private static ISpecification<Sale> CaptureSpec(ISaleRepository saleRepo)
    {
        var spec = saleRepo.ReceivedCalls()
            .Select(c => c.GetArguments().FirstOrDefault(a => a is ISpecification<Sale>))
            .Cast<ISpecification<Sale>>()
            .FirstOrDefault();
        spec.Should().NotBeNull("the handler must hand a specification to the repository");
        return spec!;
    }
}
