using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TakOne.Application.Users.Queries.GetUsersPaginated;
using TakOne.Domain.Customers.Entities;
using TakOne.Domain.Users;
using TakOne.Infrastructure.Persistence;
using TakOne.Infrastructure.Persistence.Repositories;
using TakOne.IntegrationTests.Infrastructure;
using TakOne.SharedKernel.ValueObjects;
using Xunit;

namespace TakOne.IntegrationTests;

/// <summary>
/// Integration tests for the users list's Round 5 server-side filters +
/// sort: every new WHERE/ORDER BY clause must translate to real SQL and
/// behave correctly against a live EF provider (SQLite here — the same
/// provider the rest of the integration suite uses; production runs SQL
/// Server). Mirrors <see cref="SalesListFilteringIntegrationTests"/> from
/// Round 4.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY REAL-DB TESTS (vs. the handler unit tests)</b>: the unit suite
/// captures the <see cref="UsersListFilters"/> record against substitutes,
/// which proves the WIRING but not the TRANSLATION. A clause like
/// <c>u.WorkerId.ToLower().Contains(term)</c> (hand-built expression tree)
/// or <c>ORDER BY u.Gender, u.Id</c> could be wired perfectly and still
/// blow up as <c>InvalidOperationException: The LINQ expression could not
/// be translated</c> at runtime. These tests are the contract that keeps
/// the SQL translation honest.
/// </para>
/// <para>
/// <b>SEEDING</b>: users are built via the <see cref="User"/> factories
/// (<see cref="User.CreateCustomer"/> / <see cref="User.CreateStaff"/>);
/// deactivation goes through the domain method. No reflection is needed —
/// every filter-relevant member (WorkerId, FullName, Gender, GroupId,
/// IsActive) is a factory parameter or a domain behavior.
/// </para>
/// </remarks>
public class UsersListFilteringIntegrationTests
{
    private static Task<(UserRepository repo, ApplicationDbContext db)> CreateSeededAsync(
        params User[] users)
        => CreateSeededAsync(users, Array.Empty<CustomerGroup>());

    private static async Task<(UserRepository repo, ApplicationDbContext db)> CreateSeededAsync(
        User[] users,
        params CustomerGroup[] groups)
    {
        var db = await SqliteTestDbFactory.CreateAsync();
        var repo = new UserRepository(db);

        // Groups FIRST: User.GroupId has an FK to CustomerGroups — a user
        // with a GroupId whose group row doesn't exist fails SaveChanges.
        foreach (var group in groups)
        {
            db.CustomerGroups.Add(group);
        }

        foreach (var user in users)
        {
            await repo.AddAsync(user, CancellationToken.None);
        }

        await db.SaveChangesAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
        return (repo, db);
    }

    private static CustomerGroup MakeGroup(string name)
        => CustomerGroup.Create(name, new Money(1000m, "IRR"));

    private static UsersListFilters Filters(
        string? searchTerm = null,
        Guid? groupId = null,
        bool? isActive = null,
        Gender? gender = null,
        UsersTextFilter? workerId = null,
        UsersTextFilter? fullName = null,
        UsersSortBy? sortBy = null,
        bool sortDescending = false)
        => new(searchTerm, groupId, isActive, gender, workerId, fullName, sortBy, sortDescending);

    private static User MakeUser(
        string workerId,
        string fullName,
        Guid? groupId = null,
        Gender gender = Gender.Male,
        bool active = true)
    {
        var user = groupId is null
            ? User.CreateStaff(workerId, fullName, gender)
            : User.CreateCustomer(workerId, fullName, groupId.Value, gender);
        if (!active)
        {
            user.Deactivate();
        }
        return user;
    }

    // ── Default order (FullName asc) — the contract existing callers rely on

    [Fact]
    public async Task GetPaginatedAsync_NoFilters_DefaultsToFullNameAscending()
    {
        var (repo, db) = await CreateSeededAsync(
            MakeUser("EMP-3", "Charlie"),
            MakeUser("EMP-1", "Alice"),
            MakeUser("EMP-2", "Bob"));

        await using (db)
        {
            var result = await repo.GetPaginatedAsync(Filters(), 1, 10, CancellationToken.None);

            result.Items.Select(u => u.FullName)
                .Should().BeInAscendingOrder("FullName asc is the pre-Round-5 default the mobile list + typeahead rely on");
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_NullFilters_DefaultsToFullNameAscending()
    {
        // The handler may hand a null filters record (defensive default).
        var (repo, db) = await CreateSeededAsync(
            MakeUser("EMP-2", "Bob"),
            MakeUser("EMP-1", "Alice"));

        await using (db)
        {
            var result = await repo.GetPaginatedAsync(null, 1, 10, CancellationToken.None);

            result.Items.Select(u => u.FullName).Should().BeInAscendingOrder();
        }
    }

    // ── Sorting ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPaginatedAsync_SortByWorkerId_OrdersInSql()
    {
        var (repo, db) = await CreateSeededAsync(
            MakeUser("EMP-3", "Alice"),
            MakeUser("EMP-1", "Charlie"),
            MakeUser("EMP-2", "Bob"));

        await using (db)
        {
            var asc = await repo.GetPaginatedAsync(
                Filters(sortBy: UsersSortBy.WorkerId), 1, 10, CancellationToken.None);
            var desc = await repo.GetPaginatedAsync(
                Filters(sortBy: UsersSortBy.WorkerId, sortDescending: true), 1, 10, CancellationToken.None);

            asc.Items.Select(u => u.WorkerId).Should().BeInAscendingOrder();
            desc.Items.Select(u => u.WorkerId).Should().BeInDescendingOrder();
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_SortByGenderAndIsActive_OrderInSqlWithTiebreaker()
    {
        var (repo, db) = await CreateSeededAsync(
            MakeUser("EMP-1", "Alice", gender: Gender.Female),
            MakeUser("EMP-2", "Bob", gender: Gender.Male),
            MakeUser("EMP-3", "Carol", gender: Gender.Female),
            MakeUser("EMP-4", "Dave", active: false),
            MakeUser("EMP-5", "Eve"));

        await using (db)
        {
            // Gender sorts by the enum's int ordinal (Male=0, Female=1).
            var byGender = await repo.GetPaginatedAsync(
                Filters(sortBy: UsersSortBy.Gender), 1, 10, CancellationToken.None);
            byGender.Items.Select(u => u.Gender)
                .Should().BeInAscendingOrder(g => (int)g);

            // IsActive: false (0) sorts before true (1) ascending; the Id
            // tiebreaker keeps each block deterministic.
            var byActive = await repo.GetPaginatedAsync(
                Filters(sortBy: UsersSortBy.IsActive), 1, 10, CancellationToken.None);
            byActive.Items.Select(u => u.IsActive)
                .Should().BeInAscendingOrder();

            var byActiveDesc = await repo.GetPaginatedAsync(
                Filters(sortBy: UsersSortBy.IsActive, sortDescending: true), 1, 10, CancellationToken.None);
            byActiveDesc.Items.Select(u => u.IsActive)
                .Should().BeInDescendingOrder();
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_EqualSortKeys_TiebreakByIdAcrossPages()
    {
        // 6 users with the SAME FullName, page size 2 → three pages. The Id
        // tiebreaker must keep the union of pages equal to the Id-ascending
        // order (no skips, no duplicates) — deterministic OFFSET/FETCH
        // paging. (The seed order is IRRELEVANT — the tiebreaker defines
        // the order.)
        var users = Enumerable.Range(1, 6)
            .Select(i => MakeUser($"EMP-{i}", "Same Name"))
            .ToArray();
        var expectedIds = users.Select(u => u.Id).OrderBy(id => id).ToArray();

        var (repo, db) = await CreateSeededAsync(users);
        await using (db)
        {
            var collected = new List<Guid>();
            for (var page = 1; page <= 3; page++)
            {
                var result = await repo.GetPaginatedAsync(
                    Filters(sortBy: UsersSortBy.FullName), page, 2, CancellationToken.None);
                collected.AddRange(result.Items.Select(u => u.Id));
            }

            collected.Should().BeEquivalentTo(expectedIds,
                options => options.WithStrictOrdering(),
                "the Id tiebreaker keeps equal-key rows deterministic across page boundaries");
        }
    }

    // ── Filters ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPaginatedAsync_IsActiveFilter_FiltersInSql()
    {
        var (repo, db) = await CreateSeededAsync(
            MakeUser("EMP-1", "Alice", active: true),
            MakeUser("EMP-2", "Bob", active: false),
            MakeUser("EMP-3", "Carol", active: false));

        await using (db)
        {
            var activeOnly = await repo.GetPaginatedAsync(
                Filters(isActive: true), 1, 10, CancellationToken.None);
            var inactiveOnly = await repo.GetPaginatedAsync(
                Filters(isActive: false), 1, 10, CancellationToken.None);

            activeOnly.TotalCount.Should().Be(1);
            activeOnly.Items.Should().ContainSingle(u => u.WorkerId == "EMP-1");
            inactiveOnly.TotalCount.Should().Be(2);
            inactiveOnly.Items.Should().OnlyContain(u => !u.IsActive);
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_GroupIdFilter_FiltersInSql()
    {
        var groupA = MakeGroup("Group A");
        var groupB = MakeGroup("Group B");
        var (repo, db) = await CreateSeededAsync(
            new[]
            {
                MakeUser("EMP-1", "Alice", groupId: groupA.Id),
                MakeUser("EMP-2", "Bob", groupId: groupB.Id),
                MakeUser("EMP-3", "Carol") // staff — no group
            },
            groupA, groupB);

        await using (db)
        {
            var result = await repo.GetPaginatedAsync(
                Filters(groupId: groupA.Id), 1, 10, CancellationToken.None);

            result.TotalCount.Should().Be(1);
            result.Items.Should().ContainSingle(u => u.WorkerId == "EMP-1");
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_GenderFilter_FiltersInSql()
    {
        var (repo, db) = await CreateSeededAsync(
            MakeUser("EMP-1", "Alice", gender: Gender.Female),
            MakeUser("EMP-2", "Bob", gender: Gender.Male),
            MakeUser("EMP-3", "Carol", gender: Gender.Female));

        await using (db)
        {
            var result = await repo.GetPaginatedAsync(
                Filters(gender: Gender.Female), 1, 10, CancellationToken.None);

            result.TotalCount.Should().Be(2);
            result.Items.Should().OnlyContain(u => u.Gender == Gender.Female);
        }
    }

    // ── SearchTerm (the legacy cross-column OR — now genuinely
    //    case-insensitive on BOTH providers via LOWER()) ────────────────

    [Fact]
    public async Task GetPaginatedAsync_SearchTerm_MatchesWorkerIdOrFullNameCaseInsensitively()
    {
        var (repo, db) = await CreateSeededAsync(
            MakeUser("EMP-1", "Alice Smith"),
            MakeUser("EMP-2", "Bob Jones"),
            MakeUser("CUST-9", "Carol smith"));

        await using (db)
        {
            // By worker id (lowercase term against uppercase data).
            var byWorker = await repo.GetPaginatedAsync(
                Filters(searchTerm: "emp-1"), 1, 10, CancellationToken.None);
            byWorker.TotalCount.Should().Be(1);
            byWorker.Items.Should().ContainSingle(u => u.WorkerId == "EMP-1");

            // By name — case-insensitive across rows, OR'd with WorkerId.
            var byName = await repo.GetPaginatedAsync(
                Filters(searchTerm: "SMITH"), 1, 10, CancellationToken.None);
            byName.TotalCount.Should().Be(2, "the term matches BOTH Alice Smith and Carol smith");
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_SearchTerm_BeyondFirstPage_FindsAllMatches()
    {
        // 25 users whose names all contain "ann"; page size 10 → the
        // pre-Round-5 page could never have found rows 11-25.
        var users = Enumerable.Range(1, 25)
            .Select(i => MakeUser($"EMP-{i:00}", $"Ann {i:00}"))
            .ToArray();

        var (repo, db) = await CreateSeededAsync(users);
        await using (db)
        {
            var result = await repo.GetPaginatedAsync(
                Filters(searchTerm: "ann"), 1, 10, CancellationToken.None);

            result.TotalCount.Should().Be(25,
                "the count reflects the WHOLE table, not the loaded page");
        }
    }

    // ── Typed per-column text filters (the hand-built expression trees) ─

    [Theory]
    [InlineData(UsersTextOperator.Contains, "smi", true)]
    [InlineData(UsersTextOperator.Contains, "xyz", false)]
    [InlineData(UsersTextOperator.NotContains, "smi", false)]
    [InlineData(UsersTextOperator.Equals, "alice smith", true)]
    [InlineData(UsersTextOperator.Equals, "ALICE SMITH", true)]
    [InlineData(UsersTextOperator.NotEquals, "alice smith", false)]
    [InlineData(UsersTextOperator.StartsWith, "alice", true)]
    [InlineData(UsersTextOperator.StartsWith, "smith", false)]
    [InlineData(UsersTextOperator.EndsWith, "smith", true)]
    [InlineData(UsersTextOperator.EndsWith, "alice", false)]
    public async Task GetPaginatedAsync_FullNameTextFilter_AllOperatorsTranslate(
        UsersTextOperator op, string value, bool expectedAliceMatch)
    {
        var (repo, db) = await CreateSeededAsync(
            MakeUser("EMP-1", "Alice Smith"),
            MakeUser("EMP-2", "Bob Jones"));

        await using (db)
        {
            var result = await repo.GetPaginatedAsync(
                Filters(fullName: new UsersTextFilter(value, op)), 1, 10, CancellationToken.None);

            // Whether ALICE matches under the operator (Bob's membership
            // varies per operator: NotContains/NotEquals keep him, the
            // positive forms exclude him — asserting Alice's presence/absence
            // is the operator's actual contract).
            var aliceMatched = result.Items.Any(u => u.FullName == "Alice Smith");
            aliceMatched.Should().Be(expectedAliceMatch,
                $"{op} '{value}' against Alice Smith (case-insensitive in SQL)");
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_WorkerIdTextFilter_FiltersInSql()
    {
        var (repo, db) = await CreateSeededAsync(
            MakeUser("EMP-100", "Alice"),
            MakeUser("EMP-200", "Bob"),
            MakeUser("CUST-100", "Carol"));

        await using (db)
        {
            var result = await repo.GetPaginatedAsync(
                Filters(workerId: new UsersTextFilter("100", UsersTextOperator.EndsWith)),
                1, 10, CancellationToken.None);

            result.TotalCount.Should().Be(2, "both EMP-100 and CUST-100 end with '100'");
            result.Items.Select(u => u.WorkerId).Should().BeEquivalentTo("EMP-100", "CUST-100");
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_TextFilterWhitespaceValue_NoClause()
    {
        var (repo, db) = await CreateSeededAsync(
            MakeUser("EMP-1", "Alice"),
            MakeUser("EMP-2", "Bob"));

        await using (db)
        {
            var result = await repo.GetPaginatedAsync(
                Filters(workerId: new UsersTextFilter("   ", UsersTextOperator.Contains)),
                1, 10, CancellationToken.None);

            result.TotalCount.Should().Be(2, "a whitespace-only filter value adds no WHERE clause");
        }
    }

    // ── Composition + paging ────────────────────────────────────────────

    [Fact]
    public async Task GetPaginatedAsync_FiltersAndPaging_TotalCountStaysAccurate()
    {
        // 12 female users in group A (6 active) + 8 male users in group A
        // + 5 staff — gender + active + group filters compose in SQL and
        // the TotalCount describes the FILTERED set, not the page.
        var groupA = MakeGroup("Group A");
        var users = new List<User>();
        for (var i = 1; i <= 12; i++)
        {
            users.Add(MakeUser($"EMP-F{i:00}", $"Female {i:00}",
                groupId: groupA.Id, gender: Gender.Female, active: i <= 6));
        }
        for (var i = 1; i <= 8; i++)
        {
            users.Add(MakeUser($"EMP-M{i:00}", $"Male {i:00}",
                groupId: groupA.Id, gender: Gender.Male));
        }
        for (var i = 1; i <= 5; i++)
        {
            users.Add(MakeUser($"STF-{i:00}", $"Staff {i:00}"));
        }

        var (repo, db) = await CreateSeededAsync(users.ToArray(), groupA);
        await using (db)
        {
            var page1 = await repo.GetPaginatedAsync(
                Filters(groupId: groupA.Id, gender: Gender.Female, isActive: true),
                1, 4, CancellationToken.None);

            page1.TotalCount.Should().Be(6, "6 active females in group A");
            page1.Items.Should().HaveCount(4, "page size 4");
            page1.PageNumber.Should().Be(1);
            page1.HasNextPage.Should().BeTrue();

            var page2 = await repo.GetPaginatedAsync(
                Filters(groupId: groupA.Id, gender: Gender.Female, isActive: true),
                2, 4, CancellationToken.None);
            page2.Items.Should().HaveCount(2, "the remaining rows of the filtered set");
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_SortComposesWithSearchFilter()
    {
        var (repo, db) = await CreateSeededAsync(
            MakeUser("EMP-3", "Ann Charlie"),
            MakeUser("EMP-1", "Ann Alice"),
            MakeUser("EMP-2", "Bob"),
            MakeUser("EMP-4", "Ann Bob"));

        await using (db)
        {
            var result = await repo.GetPaginatedAsync(
                Filters(searchTerm: "ann", sortBy: UsersSortBy.FullName),
                1, 10, CancellationToken.None);

            result.Items.Select(u => u.FullName)
                .Should().Equal(new[] { "Ann Alice", "Ann Bob", "Ann Charlie" },
                    "sort ∘ search composes in SQL");
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_EmptySeedSet_ReturnsEmptyPage()
    {
        var (repo, db) = await CreateSeededAsync();
        await using (db)
        {
            var result = await repo.GetPaginatedAsync(Filters(), 1, 20, CancellationToken.None);

            result.TotalCount.Should().Be(0);
            result.Items.Should().BeEmpty();
        }
    }

    // ── ROUND 6 — GroupName sort (LEFT JOIN to CustomerGroups) ────────

    [Fact]
    public async Task GetPaginatedAsync_GroupNameSort_OrdersByJoinedName()
    {
        // Three groups + users spread across them, PLUS a groupless user
        // (GroupId null → NULL sort key). Ascending: NULL first, then the
        // group names alphabetically; descending mirrors. This proves the
        // GroupJoin + DefaultIfEmpty LEFT JOIN translates and orders on
        // the JOINED column (the thing unit tests cannot prove).
        var groupA = MakeGroup("Alpha Group");
        var groupB = MakeGroup("Beta Group");
        var groupC = MakeGroup("Gamma Group");

        var users = new[]
        {
            MakeUser("EMP-1", "Alice", groupId: groupB.Id),   // Beta Group
            MakeUser("EMP-2", "Bob", groupId: groupA.Id),     // Alpha Group
            MakeUser("EMP-3", "Charlie"),                     // no group → NULL
            MakeUser("EMP-4", "Diana", groupId: groupC.Id),   // Gamma Group
            MakeUser("EMP-5", "Eve", groupId: groupA.Id),     // Alpha Group (tie)
        };

        var (repo, db) = await CreateSeededAsync(users, groupA, groupB, groupC);
        await using (db)
        {
            // The Ids are random Guids (not creation-sequential), so the
            // expected tie order is DERIVED from them — the test pins the
            // CONTRACT (NULL first, names alphabetical, Id tiebreak),
            // not a hard-coded worker order.
            var alphaOrder = users
                .Where(u => u.WorkerId is "EMP-2" or "EMP-5")
                .OrderBy(u => u.Id)
                .Select(u => u.WorkerId)
                .ToArray();

            var ascending = await repo.GetPaginatedAsync(
                Filters(sortBy: UsersSortBy.GroupName), 1, 10, CancellationToken.None);

            // NULL first, then Alpha ×2 (Id tiebreak), Beta, Gamma.
            ascending.Items.Select(u => u.WorkerId).Should().Equal(
                new[] { "EMP-3" }.Concat(alphaOrder).Concat(new[] { "EMP-1", "EMP-4" }).ToArray(),
                "groupless users carry a NULL sort key (first ascending); " +
                "equal names tiebreak by Id");

            var descending = await repo.GetPaginatedAsync(
                Filters(sortBy: UsersSortBy.GroupName, sortDescending: true), 1, 10, CancellationToken.None);

            // Mirror: Gamma, Beta, Alpha ×2 (Id desc tiebreak), NULL last.
            descending.Items.Select(u => u.WorkerId).Should().Equal(
                new[] { "EMP-4", "EMP-1" }.Concat(alphaOrder.Reverse()).Concat(new[] { "EMP-3" }).ToArray(),
                "descending puts the NULL sort key last, Id tiebreak descends");
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_GroupNameSort_ComposesWithGroupIdFilter()
    {
        // Sort ∘ filter composes in one SQL statement: two users of the
        // same group, name-sorted with the Id tiebreaker — the LEFT JOIN
        // must compose with the WHERE clause.
        var groupA = MakeGroup("Alpha Group");

        var users = new[]
        {
            MakeUser("EMP-9", "Zed", groupId: groupA.Id),
            MakeUser("EMP-1", "Ann", groupId: groupA.Id),
            MakeUser("EMP-5", "NoGroup User"),
        };

        var (repo, db) = await CreateSeededAsync(users, groupA);
        await using (db)
        {
            var result = await repo.GetPaginatedAsync(
                Filters(groupId: groupA.Id, sortBy: UsersSortBy.GroupName),
                1, 10, CancellationToken.None);

            result.TotalCount.Should().Be(2, "only Alpha Group members pass the filter");

            // Random Guids → derive the expected Id-tiebreak order.
            var expectedNames = users
                .Where(u => u.GroupId == groupA.Id)
                .OrderBy(u => u.Id)
                .Select(u => u.FullName)
                .ToArray();
            result.Items.Select(u => u.FullName).Should().Equal(expectedNames,
                "the Id tiebreaker orders the equal group names deterministically");
        }
    }
}

