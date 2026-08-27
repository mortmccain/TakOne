using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TakOne.Infrastructure.Persistence;

namespace TakOne.IntegrationTests.Infrastructure;

/// <summary>
/// Factory that spins up fresh in-memory SQLite databases for each
/// integration test. The SQLite provider enforces unique indexes, foreign
/// keys (with <c>PRAGMA foreign_keys=ON;</c>), NOT NULL constraints, and
/// basic transactions — enough to catch wiring mistakes that mock-heavy
/// handler unit tests cannot detect (mocks don't reject duplicate-name
/// INSERTs; SQLite + a unique index does).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY IN-MEMORY SQLITE (vs. SQL Server LocalDB / Testcontainers):</b>
/// <list type="bullet">
///   <item>No external service to start — the test runs anywhere <c>dotnet
///       test</c> runs (CI, dev box, container).</item>
///   <item>Zero disk I/O — every test gets a fresh DB in milliseconds.</item>
///   <item>EF Core translates our model into SQLite DDL via
///       <see cref="DbContext.EnsureCreatedAsync"/>. The DDL includes the
///       unique indexes and FK constraints declared by the EF
///       configurations — so a wiring mistake (e.g. a forgotten
///       <c>.IsRequired()</c> or a missing unique-index) is caught here,
///       not in production.</item>
/// </list>
/// </para>
/// <para>
/// <b>KNOWN DIFFERENCES FROM SQL SERVER (the test author must be aware):</b>
/// <list type="bullet">
///   <item>SQLite is dynamically typed. <c>nvarchar(max)</c> is honored but
///       <c>decimal(18, 2)</c> precision is not enforced at INSERT time
///       (SQLite stores it as TEXT). Money arithmetic is therefore exact-
///       decimal in our tests but might behave differently under SQL Server
///       with rounding. Not a concern for the wiring-mistake class of
///       bugs these integration tests catch.</item>
///   <item><c>IsUnique()</c> indexes ARE enforced — a duplicate INSERT
///       throws <c>SqliteException</c> wrapped in <c>DbUpdateException</c>.
///       The handler's "name already exists" friendly check fires BEFORE
///       the INSERT, so we never see the SQLite-side rejection in the
///       happy-path tests; but the duplicate-name tests assert the
///       handler's friendly failure path explicitly.</item>
///   <item><c>PRAGMA foreign_keys=ON;</c> must be set PER CONNECTION —
///       SQLite defaults to FKs OFF for backwards compatibility. We set
///       it inside <see cref="CreateAsync"/> after opening the connection.</item>
///   <item><c>ExecuteUpdateAsync</c> (used by
///       <c>MarkAllNotificationsAsReadAsync</c>) is supported by the
///       SQLite provider in EF Core 7+.</item>
/// </list>
/// </para>
/// <para>
/// <b>LIFETIME</b>: the in-memory database lives as long as the
/// <see cref="SqliteConnection"/> stays open. Each test method gets a
/// fresh connection (and therefore a fresh database) — no cross-test
/// contamination. The DbContext disposes the connection on its own
/// Dispose, BUT we expose <see cref="CreateAsync"/> that returns a DbContext
/// whose underlying connection is owned by the test (the test's
/// <c>using</c> block disposes it).
/// </para>
/// <para>
/// <b>ENSURE-CREATED VS. MIGRATIONS:</b> we use
/// <see cref="RelationalDatabaseFacadeExtensions.EnsureCreatedAsync"/>
/// (not <c>MigrateAsync</c>) because migrations are SQL-Server-specific
/// (the migration files contain raw T-SQL that won't run on SQLite).
/// <c>EnsureCreatedAsync</c> builds the schema fresh from the EF Core model
/// — which is exactly what we want: a schema that matches the current
/// code, not a historical migration chain.
/// </para>
/// </remarks>
public static class SqliteTestDbFactory
{
    /// <summary>
    /// Creates a new in-memory SQLite database with the full TakOne schema
    /// applied (ApplicationDbContext + all IEntityTypeConfiguration classes
    /// from the Infrastructure assembly). Foreign-key enforcement is ON.
    /// Each call returns an independent DB — perfect for test isolation.
    /// </summary>
    public static async Task<ApplicationDbContext> CreateAsync()
    {
        // Open the connection — the in-memory DB is alive as long as this
        // connection is open. The DbContext will hold a reference to it
        // (its connection property) and Dispose will close it.
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        // PRAGMA foreign_keys=ON; — SQLite defaults to FKs OFF; this is
        // the standard SQLite gotcha. Without this line, an FK violation
        // (e.g. inserting a SaleLineItem pointing at a non-existent Sale)
        // would silently succeed, defeating the wiring-test purpose.
        using (var pragmaCommand = connection.CreateCommand())
        {
            pragmaCommand.CommandText = "PRAGMA foreign_keys=ON;";
            await pragmaCommand.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        var context = new ApplicationDbContext(options);

        // EnsureCreatedAsync builds the schema in the in-memory DB.
        // Translates the EF Core model (DbSet + IEntityTypeConfiguration)
        // into SQLite DDL. The DataProtectionKey entity (mapped by EF
        // convention) is included.
        await context.Database.EnsureCreatedAsync();

        return context;
    }

    /// <summary>
    /// Creates a DbContext that shares the given connection (so the
    /// in-memory DB is shared across multiple DbContext instances — useful
    /// for concurrency tests where multiple threads need their own DbContext
    /// on the same in-memory DB). Each thread's DbContext will see the
    /// other's committed writes.
    /// </summary>
    /// <param name="options">
    /// A <see cref="DbContextOptions{ApplicationDbContext}"/> configured
    /// to use a shared, already-open <see cref="SqliteConnection"/>. The
    /// caller is responsible for the connection's lifetime.
    /// </param>
    public static ApplicationDbContext Create(DbContextOptions<ApplicationDbContext> options)
    {
        // The DbContext now shares the caller's connection. Multiple
        // instances pointing at the same in-memory DB work concurrently.
        return new ApplicationDbContext(options);
    }

    /// <summary>
    /// Creates an options builder whose connection is shared with the given
    /// DbContext's underlying connection. The new DbContext will see
    /// committed writes from the original (and vice versa, after
    /// SaveChanges). Useful for concurrency tests that need multiple
    /// independent DbContexts on the same in-memory DB.
    /// </summary>
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

        // Apply the schema once via a seed context — every subsequent
        // DbContext created with these options will see the same schema
        // because the in-memory DB is shared via the connection.
        await using (var seedContext = new ApplicationDbContext(options))
        {
            await seedContext.Database.EnsureCreatedAsync();
        }

        return options;
    }
}
