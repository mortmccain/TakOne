using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TakOne.Domain.Products.Entities;
using TakOne.Infrastructure.Persistence;
using TakOne.Infrastructure.Services;
using TakOne.SharedKernel.Primitives;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;

namespace TakOne.IntegrationTests.Concurrency;

/// <summary>
/// Regression tests for the optimistic-concurrency token (RowVersion) on
/// <see cref="AggregateRoot"/> — Brutal Code Review v3 Critical #14. The
/// fix added a <c>byte[] RowVersion</c> property to
/// <see cref="AggregateRoot"/> and configured every entity type that
/// exposes it as a SQL Server <c>rowversion</c> column
/// (<c>IsConcurrencyToken = true</c> +
/// <c>SetColumnType("rowversion")</c> +
/// <c>ValueGenerated.OnAddOrUpdate</c>) in
/// <see cref="ApplicationDbContext.OnModelCreating"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THESE TESTS EXIST:</b> the previous design had NO concurrency
/// token on any aggregate. Two concurrent requests loading the same
/// product (e.g. two customers buying the last item) could both read
/// <c>StockQuantity = 1</c>, both pass the stock check, both decrement
/// to 0, and both save — silently overwriting each other's write. The
/// <c>RowVersion</c> column makes the SECOND save throw
/// <see cref="DbUpdateConcurrencyException"/> ("expected to affect 1
/// row(s), but actually affected 0 row(s)"), which
/// <see cref="UnitOfWork.ExecuteWithRetryAsync"/> catches and retries.
/// </para>
/// <para>
/// <b>KNOWN BUG REVEALED BY THESE TESTS:</b> the Critical #14 fix added
/// <c>RowVersion</c> to every aggregate (including
/// <c>SystemSettings</c>) as a NOT NULL column with no default value.
/// The <c>SystemSettingsConfiguration.HasData</c> seed (which inserts a
/// singleton row during schema creation) does NOT supply a
/// <c>RowVersion</c> value. On SQL Server, the rowversion column
/// auto-assigns a value at INSERT time, so the seed succeeds. On
/// SQLite (the provider used by ALL tests in this project via
/// <c>SqliteTestDbFactory</c>), there is no native rowversion type, so
/// the NOT NULL constraint fails — and <see cref="RelationalDatabaseFacadeExtensions.EnsureCreatedAsync"/>
/// throws <c>SqliteException: NOT NULL constraint failed:
/// SystemSettings.RowVersion</c>. This breaks ALL integration tests
/// (existing <c>SaleStateMachineIntegrationTests</c>,
/// <c>ProductLifecycleIntegrationTests</c>, etc.), not just the three
/// added here. The production-code fix is either (a) add
/// <c>RowVersion = Array.Empty&lt;byte&gt;()</c> to the
/// <c>SystemSettingsConfiguration.HasData</c> anonymous object, or (b)
/// set <c>SetDefaultValue(Array.Empty&lt;byte&gt;())</c> on the
/// <c>RowVersion</c> property in the <c>OnModelCreating</c> convention
/// loop. Until that fix lands, these tests (and all other
/// integration tests) use the <see cref="TestableApplicationDbContext"/>
/// subclass below to set the default value test-locally.
/// </para>
/// <para>
/// <b>SQLITE-SPECIFIC ROWVERSION BEHAVIOR (vs. SQL Server):</b> on SQL
/// Server, a <c>rowversion</c> column is auto-bumped by the DB engine
/// on every UPDATE — the application doesn't have to do anything. On
/// SQLite, the EF Core SQLite provider does NOT auto-generate values
/// for <c>IsRowVersion()</c> + <c>byte[]</c> +
/// <c>ValueGenerated.OnAddOrUpdate</c> properties (no native rowversion
/// type, no value generator). The concurrency CHECK still fires — EF
/// Core includes the original RowVersion value in the UPDATE's WHERE
/// clause (<c>WHERE RowVersion = @original</c>) and surfaces
/// <c>DbUpdateConcurrencyException</c> when 0 rows match. To make the
/// conflict-detection tests deterministic on SQLite, the tests use a
/// manual raw-SQL UPDATE to bump the RowVersion column between saves —
/// simulating what SQL Server would have done automatically. This is
/// documented inline at each test that does it.
/// </para>
/// <para>
/// <b>WHAT THESE TESTS CATCH:</b>
/// <list type="bullet">
///   <item>The <c>RowVersion</c> column EXISTS in the schema (proving
///       the configuration in <c>OnModelCreating</c> was applied). This
///       is the minimum bar — without the column, the concurrency check
///       can't fire at all.</item>
///   <item>The column accepts non-empty <c>byte[]</c> values (proving
///       it's a BLOB column, not some other type).</item>
///   <item>EF Core issues UPDATE statements with
///       <c>WHERE RowVersion = @original</c> (proving
///       <c>IsConcurrencyToken = true</c> is honored) and surfaces
///       <c>DbUpdateConcurrencyException</c> when the WHERE clause
///       matches 0 rows.</item>
///   <item>The <see cref="UnitOfWork.ExecuteWithRetryAsync"/> retry
///       loop catches <c>DbUpdateConcurrencyException</c>, clears the
///       change tracker, and re-runs the operation — which succeeds on
///       the second attempt after re-loading the fresh rowversion.</item>
/// </list>
/// </para>
/// </remarks>
public class RowVersionConcurrencyTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    // Create a valid Product aggregate for seeding. Uses TestValues.USD as
    // the currency so the persisted Product's Price.Currency is a stable
    // value (not relevant to the rowversion check, but keeps the seed
    // consistent with the rest of the test suite).
    private static Product BuildProduct(string name = "Apple", int stock = 10)
        => Product.Create(
            name: name,
            description: "Fresh red apple",
            price: new Money(1.00m, TestValues.USD),
            stockQuantity: stock,
            categoryId: TestValues.CategoryId);

    // ── Tests ──────────────────────────────────────────────────────────

    // Verifies the canonical "two concurrent contexts" pattern: contexts
    // A and B both load the same Product, A saves first (bumping the DB's
    // RowVersion), B's save fails because B's tracked RowVersion no
    // longer matches the DB's value. This is the exact race the v1/v2
    // reviews flagged on Product.DecreaseStock — the rowversion column
    // turns the silent overwrite into a thrown exception the handler can
    // surface as a friendly "the data was modified by another user,
    // please retry" error.
    [Fact]
    public async Task SaveChanges_WhenTwoContextsUpdateSameAggregate_SecondThrowsDbUpdateConcurrencyException()
    {
        // Arrange — shared in-memory SQLite connection so both DbContexts
        // see the same rows. Each DbContext gets its own change tracker
        // (DbContext is NOT thread-safe, but sequential SaveChanges calls
        // from two contexts on one thread are fine).
        var options = await TestSqliteFactory.CreateSharedOptionsAsync();

        // Seed a Product via a one-shot seed context. The seed's
        // SaveChanges assigns the initial RowVersion value (R1, which on
        // SQLite is the configured default empty byte[]; see the
        // TestableApplicationDbContext workaround).
        var productId = Guid.Empty;
        await using (var seedDb = new TestableApplicationDbContext(options))
        {
            var seedProduct = BuildProduct();
            productId = seedProduct.Id;
            seedDb.Products.Add(seedProduct);
            await seedDb.SaveChangesAsync();
        }

        // Two contexts, A and B, both load the product. Each gets its
        // own tracked copy with the original RowVersion (R1).
        await using var dbA = new TestableApplicationDbContext(options);
        await using var dbB = new TestableApplicationDbContext(options);

        var productA = await dbA.Products.FindAsync(new object[] { productId }, CancellationToken.None);
        var productB = await dbB.Products.FindAsync(new object[] { productId }, CancellationToken.None);
        productA.Should().NotBeNull();
        productB.Should().NotBeNull();

        // Capture the tracked RowVersion (R1) for a later assertion.
        var bTrackedRowVersion = productB!.RowVersion;

        // Act — A mutates and saves. The save succeeds; on SQLite, EF
        // Core does NOT auto-bump the RowVersion on UPDATE (see the
        // class-level XML doc), so the DB's RowVersion for this row is
        // still R1 after A's save. We then manually bump the DB's
        // RowVersion via raw SQL to simulate what SQL Server's native
        // rowversion column would have done automatically.
        productA!.IncreaseStock(1);
        await dbA.SaveChangesAsync();

        // Manually bump the DB's RowVersion column via raw SQL on dbA.
        // The byte[] we set is a freshly-generated Guid's bytes — it
        // is different from R1 (the value B has tracked), so B's
        // subsequent UPDATE WHERE RowVersion = R1 will match 0 rows.
        var rivalRowVersion = Guid.NewGuid().ToByteArray();
        await dbA.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE Products SET RowVersion = {rivalRowVersion} WHERE Id = {productId}");

        // B mutates (its tracked productB still has the original R1).
        productB.IncreaseStock(1);

        // Assert — B's SaveChanges issues UPDATE WHERE RowVersion =
        // @original (R1); the DB now holds rivalRowVersion, so 0 rows
        // are affected. EF Core surfaces this as
        // DbUpdateConcurrencyException. We also assert that B's tracked
        // RowVersion differs from the bumped DB value (sanity check on
        // the test setup itself).
        bTrackedRowVersion.Should().NotEqual(rivalRowVersion,
            "B's tracked RowVersion must differ from the DB's bumped value for the WHERE clause to fail to match");
        Func<Task> act = async () => await dbB.SaveChangesAsync();
        (await act.Should().ThrowAsync<DbUpdateConcurrencyException>())
            .WithMessage("*affect*row*");
    }

    // Verifies that the RowVersion column EXISTS in the Products schema
    // and accepts a non-empty byte[] value. On SQL Server, this would be
    // automatic — the rowversion column auto-assigns an 8-byte value at
    // INSERT time. On SQLite, EF Core does NOT auto-assign values (see
    // the class-level XML doc); the test manually assigns a value via
    // raw SQL to simulate SQL Server's auto-assignment. The reload +
    // assert verifies the value is round-tripped through the DB.
    //
    // This is the minimum bar for the Critical #14 fix: without a
    // RowVersion column in the schema, no concurrency check can fire.
    // If the OnModelCreating configuration is ever accidentally removed
    // (or a future migration drops the column), this test fails loudly.
    [Fact]
    public async Task SaveChanges_WhenFreshLoad_HasRowVersionValue()
    {
        // Arrange
        var db = await TestSqliteFactory.CreateAsync();
        await using (db)
        {
            var product = BuildProduct();
            db.Products.Add(product);
            await db.SaveChangesAsync();

            // Manually assign a non-empty RowVersion value via raw SQL.
            // On SQL Server, this would be automatic (the rowversion
            // column auto-bumps on every INSERT/UPDATE). On SQLite, EF
            // Core's value generator for IsRowVersion + byte[] +
            // ValueGenerated.OnAddOrUpdate is a no-op, so we simulate
            // the auto-assignment manually. This proves the RowVersion
            // column EXISTS in the schema and accepts non-empty byte[]
            // values — the minimum contract for the concurrency check.
            var assignedRowVersion = Guid.NewGuid().ToByteArray();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE Products SET RowVersion = {assignedRowVersion} WHERE Id = {product.Id}");

            // Act — clear the change tracker and reload from DB so the
            // assertion is on the persisted value, not on an in-memory
            // value the change tracker might have fabricated.
            db.ChangeTracker.Clear();
            var reloaded = await db.Products.FindAsync(new object[] { product.Id }, CancellationToken.None);

            // Assert — RowVersion is not null, not empty, and matches
            // the value we assigned via raw SQL (proving the column
            // exists, accepts byte[] values, and round-trips through
            // the DB on reload).
            reloaded.Should().NotBeNull();
            reloaded!.RowVersion.Should().NotBeNull();
            reloaded.RowVersion.Should().NotBeEmpty();
            reloaded.RowVersion.Length.Should().BeGreaterThan(0);
            reloaded.RowVersion.Should().Equal(assignedRowVersion);
        }
    }

    // Verifies the UnitOfWork's retry loop catches
    // DbUpdateConcurrencyException and re-runs the operation. A
    // pre-loaded tracked product (RowVersion = R1) is mutated; meanwhile,
    // a rival context bumps the DB's RowVersion to R2 via raw SQL. The
    // first SaveChanges throws; the retry clears the change tracker and
    // re-loads the product (now R2), and the second SaveChanges succeeds.
    //
    // This is the EXACT pattern the production
    // CreateOrAppendSaleCommandHandler uses to defend against the
    // "double-add-to-cart" race — the retry resolves the conflict
    // transparently without surfacing an error to the user.
    [Fact]
    public async Task ExecuteWithRetryAsync_WhenConcurrencyConflict_RetriesAndSucceeds()
    {
        // Arrange — shared in-memory SQLite connection so the UoW's
        // DbContext and the rival context both see the same rows.
        var options = await TestSqliteFactory.CreateSharedOptionsAsync();

        // Seed a Product via a one-shot seed context (RowVersion = R1).
        var productId = Guid.Empty;
        await using (var seedDb = new TestableApplicationDbContext(options))
        {
            var seedProduct = BuildProduct();
            productId = seedProduct.Id;
            seedDb.Products.Add(seedProduct);
            await seedDb.SaveChangesAsync();
        }

        // The UnitOfWork's DbContext pre-loads the product (tracked, R1).
        // Mark it dirty by calling IncreaseStock — this ensures the next
        // SaveChanges will issue an UPDATE (which carries the WHERE
        // RowVersion = @original clause that triggers the conflict).
        await using var unitOfWorkDb = new TestableApplicationDbContext(options);
        var unitOfWork = new UnitOfWork(unitOfWorkDb);
        var preloaded = await unitOfWorkDb.Products.FindAsync(
            new object[] { productId }, CancellationToken.None);
        preloaded.Should().NotBeNull();
        preloaded!.IncreaseStock(1); // mark dirty

        // Rival context bumps the DB's RowVersion to a fresh value via
        // raw SQL. This simulates what SQL Server's native rowversion
        // column would have done automatically when another transaction
        // committed an UPDATE on the same row.
        await using (var rivalDb = new TestableApplicationDbContext(options))
        {
            var rivalRowVersion = Guid.NewGuid().ToByteArray();
            await rivalDb.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE Products SET RowVersion = {rivalRowVersion} WHERE Id = {productId}");
        }

        // Act — ExecuteWithRetryAsync wraps an idempotent operation
        // that re-loads, mutates, and saves. On attempt 1, FindAsync
        // returns the tracked stale instance (RowVersion = R1); the
        // SaveChanges issues UPDATE WHERE RowVersion = R1 → 0 rows →
        // DbUpdateConcurrencyException. The retry clears the change
        // tracker; on attempt 2, FindAsync hits the DB and returns the
        // fresh R2 value; the SaveChanges succeeds.
        var attempts = 0;
        var result = await unitOfWork.ExecuteWithRetryAsync(async ct =>
        {
            attempts++;
            // FindAsync: attempt 1 returns the tracked stale R1
            // instance; attempt 2 (after ClearChangeTracker) hits the
            // DB and returns the fresh R2 instance.
            var p = await unitOfWorkDb.Products.FindAsync(new object[] { productId }, ct);
            p!.IncreaseStock(1);
            await unitOfWork.SaveChangesAsync(ct);
            return attempts;
        });

        // Assert — the retry happened (attempts == 2) and the
        // operation succeeded on the second attempt (result == 2).
        result.Should().Be(2, "the operation should have succeeded on the second attempt after the retry");
        attempts.Should().Be(2, "the operation should have been invoked twice (once initially, once on retry)");

        // Verify the persisted state reflects the SUCCESSFUL retry only.
        // Initial stock was 10; the rival's raw-SQL bump didn't touch
        // StockQuantity; the preloaded instance's IncreaseStock(1) was
        // on a stale tracked entity whose SaveChanges threw, so it
        // didn't persist; the successful retry's IncreaseStock(1)
        // brought the persisted stock to 11.
        await using var verifyDb = new TestableApplicationDbContext(options);
        var final = await verifyDb.Products.AsNoTracking()
            .FirstAsync(p => p.Id == productId);
        final.StockQuantity.Should().Be(11,
            "only the successful retry's IncreaseStock(1) should have persisted (10 + 1 = 11)");
    }

    // ── Test-only ApplicationDbContext subclass ──────────────────────────

    /// <summary>
    /// Test-only subclass of <see cref="ApplicationDbContext"/> that
    /// works around the schema-creation bug described in the
    /// <see cref="RowVersionConcurrencyTests"/> class-level XML doc.
    /// Sets a default value (<c>Array.Empty&lt;byte&gt;()</c>) on every
    /// entity's <c>RowVersion</c> property so the
    /// <c>SystemSettingsConfiguration.HasData</c> seed INSERT passes the
    /// NOT NULL constraint on SQLite (where EF Core does NOT auto-assign
    /// rowversion values). On SQL Server, this default is never used
    /// (the rowversion column auto-generates).
    /// </summary>
    /// <remarks>
    /// This subclass exists ONLY because the brief forbids touching
    /// production source files (the proper fix is in
    /// <c>SystemSettingsConfiguration.HasData</c> or
    /// <c>ApplicationDbContext.OnModelCreating</c>). Once that fix
    /// lands in a future round, this subclass can be deleted and the
    /// tests can use plain <see cref="ApplicationDbContext"/> directly.
    /// </remarks>
    internal sealed class TestableApplicationDbContext : ApplicationDbContext
    {
        public TestableApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // WORKAROUND: set a default value on every entity's
            // RowVersion property. This makes the SystemSettings HasData
            // seed INSERT supply empty byte[] for RowVersion (instead of
            // NULL), passing the NOT NULL constraint on SQLite. The
            // default is also used by the Product INSERT in tests where
            // we don't manually assign a RowVersion — the tests that
            // need a non-empty RowVersion (Tests 1 and 3) use raw SQL to
            // manually bump the value, simulating SQL Server's auto-bump.
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersionProperty = entityType.FindProperty(nameof(AggregateRoot.RowVersion));
                if (rowVersionProperty is not null)
                {
                    rowVersionProperty.SetDefaultValue(Array.Empty<byte>());
                }
            }
        }
    }

    // ── Test-only SQLite DB factory ─────────────────────────────────────

    /// <summary>
    /// Test-only SQLite DB factory that mirrors
    /// <c>SqliteTestDbFactory</c> but uses
    /// <see cref="TestableApplicationDbContext"/> so the schema-creation
    /// workaround (default value on RowVersion) is applied. Used only by
    /// <see cref="RowVersionConcurrencyTests"/>; the rest of the
    /// integration-test suite still uses <c>SqliteTestDbFactory</c>.
    /// </summary>
    private static class TestSqliteFactory
    {
        public static async Task<ApplicationDbContext> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            using (var pragmaCommand = connection.CreateCommand())
            {
                pragmaCommand.CommandText = "PRAGMA foreign_keys=ON;";
                await pragmaCommand.ExecuteNonQueryAsync();
            }

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options;

            var context = new TestableApplicationDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return context;
        }

        public static async Task<DbContextOptions<ApplicationDbContext>> CreateSharedOptionsAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            using (var pragmaCommand = connection.CreateCommand())
            {
                pragmaCommand.CommandText = "PRAGMA foreign_keys=ON;";
                await pragmaCommand.ExecuteNonQueryAsync();
            }

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options;

            await using (var seedContext = new TestableApplicationDbContext(options))
            {
                await seedContext.Database.EnsureCreatedAsync();
            }

            return options;
        }
    }
}
