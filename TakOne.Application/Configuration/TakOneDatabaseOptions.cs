namespace TakOne.Application.Configuration;

/// <summary>
/// Strongly-typed options for the TakOne database connection.
///
/// Bound from the <c>"TakOne:Database"</c> section of the application
/// configuration (appsettings.json, environment variables, Azure Key Vault,
/// user secrets — whatever <c>IConfiguration</c> is composed from).
///
/// WHY THIS LIVES IN THE APPLICATION LAYER (not Infrastructure):
///   Historically, both the Application layer (for Wolverine's SQL Server
///   message store) and the Infrastructure layer (for EF Core's
///   <c>ApplicationDbContext</c>) needed the same connection string. After
///   the SQL Server message store call was moved to Infrastructure (it's
///   an engine-specific persistence concern, not a messaging abstraction),
///   only Infrastructure binds and reads this options class. The type
///   remains in Application because:
///     - It is a configuration CONTRACT (POCO + section-name constant +
///       validation method), not an infrastructure implementation.
///     - Configuration contracts traditionally live in the innermost layer
///       that any consumer may need — moving it to Infrastructure would
///       couple the contract to its current consumer and make future
///       reuse (e.g. by a second persistence engine, a migration tool,
///       or a test helper) awkward.
///     - If you'd rather have it in <c>TakOne.Infrastructure.Configuration</c>,
///       that's also a defensible choice — the move is mechanical (change
///       namespace + one <c>using</c>).
///
/// WHY A STRONGLY-TYPED OPTIONS CLASS (not raw IConfiguration reads):
///   - Named keys: <c>options.ConnectionString</c> is greppable and
///     refactor-safe; <c>configuration["TakOne:Database:ConnectionString"]</c>
///     is a magic string that breaks silently if the key changes.
///   - Validation: <see cref="EnsureValid"/> is called at startup by
///     <c>AddTakOneInfrastructure</c>, so a missing connection string
///     fails FAST with a clear message instead of surfacing as an opaque
///     SqlException on the first request.
///   - Testability: tests can construct a <see cref="TakOneDatabaseOptions"/>
///     directly and pass it to the composition root, without needing a
///     fake <c>IConfiguration</c>.
///
/// SECURITY:
///   The connection string contains credentials. Treat it as a secret:
///     - NEVER log it (EF Core + Wolverine don't log it by default —
///       don't add logging that does).
///     - NEVER put it in source control. Use user-secrets locally,
///       env vars / Key Vault in production.
///     - NEVER expose it as a method parameter on public APIs (that's
///       why <c>AddTakOneApplication</c> and <c>AddTakOneInfrastructure</c>
///       take <c>IConfiguration</c>, not a connection string). Note: after
///       the SQL Server message store was moved to Infrastructure, only
///       <c>AddTakOneInfrastructure</c> actually reads the connection string
///       — <c>AddTakOneApplication</c> takes <c>IConfiguration</c> purely
///       for the optional <c>Wolverine:SlowRequestThresholdMs</c> tuning knob.
/// </summary>
public sealed class TakOneDatabaseOptions
{
    /// <summary>
    /// The configuration section key these options are bound from.
    /// Centralized here so the binding call site and any documentation
    /// reference the same constant.
    /// </summary>
    public const string SectionName = "TakOne:Database";

    /// <summary>
    /// The SQL Server connection string for the application database.
    ///
    /// Used by BOTH consumers, both in <c>AddTakOneInfrastructure</c>:
    ///   - <c>ApplicationDbContext</c> — for domain entities + ASP.NET
    ///     Identity tables.
    ///   - Wolverine's SQL Server message store — for the transactional
    ///     outbox (<c>wolverine_messages</c> table).
    ///
    /// Using ONE connection string for both is intentional: it lets the
    /// outbox entries commit in the SAME transaction as business changes,
    /// which is what makes the outbox pattern work. If you ever split them
    /// into separate databases, the outbox loses its transactional guarantee
    /// and you'd need MSDTC — which is almost never worth it.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Validates that the options are usable. Called at startup by
    /// <c>AddTakOneInfrastructure</c>. Throws <see cref="InvalidOperationException"/>
    /// with an actionable message if the connection string is missing.
    ///
    /// We throw rather than return a bool because a missing connection
    /// string is a deployment misconfiguration — there's no recovery path
    /// other than fixing the configuration and restarting.
    /// </summary>
    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException(
                $"The TakOne database connection string is not configured. " +
                $"Add it under '{SectionName}:ConnectionString' in appsettings.json, " +
                $"or set the environment variable " +
                $"'{SectionName.Replace(":", "__")}__ConnectionString' " +
                $"(i.e. 'TakOne__Database__ConnectionString'), " +
                $"or use Azure Key Vault / user secrets as appropriate for the environment. " +
                $"The application cannot start without a valid database connection string.");
        }
    }
}