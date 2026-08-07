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
    ///
    /// MARS GUARD:
    ///   We also reject connection strings that enable
    ///   <c>MultipleActiveResultSets=True</c>. MARS disables EF Core's
    ///   ability to create savepoints inside an ambient transaction, which
    ///   means a failed <c>SaveChangesAsync</c> leaves the Wolverine-managed
    ///   transaction in an indeterminate state. The application's retry
    ///   loop (see <c>UnitOfWork.ExecuteWithRetryAsync</c>) cannot recover
    ///   from this — every subsequent attempt on the same poisoned
    ///   transaction fails again, exhausting all retries and surfacing the
    ///   underlying <c>DbUpdateConcurrencyException</c> to the user.
    ///
    ///   This was the root cause of the "double-add-to-cart" failure
    ///   where rapid clicks / multi-tab use / refresh-during-add produced
    ///   repeated "expected to affect 1 row(s), but actually affected 0
    ///   row(s)" errors. The fix is to keep MARS off — this guard makes
    ///   that requirement unbreakable at startup.
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

        // MARS guard. See the XML doc above for the full rationale.
        // We do a case-insensitive substring search rather than a full
        // SqlConnectionStringBuilder parse so that this validation also
        // works for malformed connection strings (the parse would throw
        // first and obscure the actionable "MARS is on" message).
        if (ContainsMars(ConnectionString))
        {
            throw new InvalidOperationException(
                $"The TakOne database connection string has " +
                $"'MultipleActiveResultSets=True' (MARS) enabled. " +
                $"MARS is incompatible with the application's retry-on-conflict " +
                $"strategy: it disables EF Core savepoints inside the " +
                $"Wolverine-managed transaction, so a failed SaveChangesAsync " +
                $"poisons the transaction and the retry loop cannot recover. " +
                $"Set 'MultipleActiveResultSets=False' in the connection string " +
                $"(check appsettings.json, appsettings.Development.json, AND " +
                $"the user secrets for the TakOne.WebUI project — user secrets " +
                $"override appsettings and are the most common source of this " +
                $"override). The application cannot start with MARS enabled.");
        }
    }

    /// <summary>
    /// Case-insensitive check for <c>MultipleActiveResultSets=True</c>
    /// in a SQL Server connection string. Accepts <c>True</c>, <c>true</c>,
    /// <c>TRUE</c>, and whitespace-padded variants. Returns <c>false</c>
    /// for any other value (including <c>False</c>, <c>false</c>, or the
    /// key being absent entirely — the SQL Server default is MARS off).
    /// </summary>
    private static bool ContainsMars(string connectionString)
    {
        // Simple case-insensitive search. A full parse via
        // SqlConnectionStringBuilder would also work, but it would throw
        // on a malformed string and obscure the actionable MARS message.
        var span = connectionString.AsSpan();

        // We're looking for the key "MultipleActiveResultSets" followed by
        // "=" and a truthy value. SQL Server connection strings are
        // case-insensitive on both keys and boolean values.
        var key = "MultipleActiveResultSets".AsSpan();
        var i = 0;
        while (i <= span.Length - key.Length)
        {
            // Find the next occurrence of the key (case-insensitive).
            var match = span.Slice(i).IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (match < 0)
            {
                return false;
            }
            i += match;

            // Make sure the match is a whole key, not a substring of a
            // longer key name. The character before the match (if any)
            // must be a key separator (';' or start of string).
            if (i > 0 && span[i - 1] != ';' && !char.IsWhiteSpace(span[i - 1]))
            {
                i += key.Length;
                continue;
            }

            // Skip the key itself.
            var j = i + key.Length;

            // Skip optional whitespace, then expect '='.
            while (j < span.Length && char.IsWhiteSpace(span[j])) j++;
            if (j >= span.Length || span[j] != '=')
            {
                i += key.Length;
                continue;
            }
            j++;

            // Skip optional whitespace after '='.
            while (j < span.Length && char.IsWhiteSpace(span[j])) j++;

            // Read the value up to the next ';' or end of string.
            var valueEnd = j;
            while (valueEnd < span.Length && span[valueEnd] != ';') valueEnd++;
            var value = span.Slice(j, valueEnd - j).Trim();

            // SQL Server accepts True/False/Yes/No (case-insensitive).
            if (value.Equals("True".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Yes".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Keep scanning in case the key appears more than once (the
            // last occurrence wins per SqlConnectionStringBuilder, but we
            // are strict: any True occurrence is a hard fail).
            i += key.Length;
        }

        return false;
    }
}