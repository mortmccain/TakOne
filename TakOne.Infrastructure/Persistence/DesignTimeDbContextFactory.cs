using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using TakOne.Application.Configuration;

namespace TakOne.Infrastructure.Persistence;

/// <summary>
/// Design-time factory for <see cref="ApplicationDbContext"/>.
///
/// WHY THIS EXISTS:
///   <c>dotnet ef migrations add</c> and <c>dotnet ef database update</c> need
///   to construct an <c>ApplicationDbContext</c> instance WITHOUT running the
///   full <c>Program.cs</c>. Normally EF's design-time host can spin up
///   <c>Program.cs</c> just enough to resolve the DbContext from DI — but
///   TakOne's <c>Program.cs</c> calls <c>builder.Host.UseWolverine()</c>, and
///   Wolverine's host initialization eagerly validates its SQL Server message
///   store (it tries to connect + ensure schema). That validation throws on a
///   fresh install where the database doesn't exist yet — a chicken-and-egg:
///     - You can't run <c>migrations add</c> because Wolverine fails to init.
///     - You can't create the database without running <c>migrations update</c>.
///
///   The fix is to give EF tooling a way to construct the DbContext DIRECTLY,
///   bypassing Program.cs + Wolverine entirely. That's what this factory does.
///   EF's design-time services look for an <c>IDesignTimeDbContextFactory&lt;T&gt;</c>
///   implementation in the same assembly as the DbContext (or the startup
///   project) and use it INSTEAD of trying to resolve the DbContext from the
///   app's DI container.
///
/// WHEN EF USES THIS vs WHEN IT DOESN'T:
///   - <c>dotnet ef migrations add</c> → uses this factory
///   - <c>dotnet ef database update</c> → uses this factory
///   - <c>dotnet ef migrations script</c> → uses this factory
///   - <c>dotnet ef dbcontext info</c> → uses this factory
///   - <c>dotnet run</c> (the actual app) → IGNORES this factory and uses the
///     DI registration in <c>AddTakOneInfrastructure</c> instead.
///   So this factory ONLY affects the EF tooling, never the running app.
///
/// WHY IT READS appsettings.json MANUALLY:
///   At design time, there's no <c>IConfiguration</c> registered (we're
///   bypassing Program.cs). We need the connection string from somewhere —
///   reading <c>appsettings.json</c> from the WebUI project (which is the
///   startup project passed to <c>dotnet ef</c>) is the simplest path.
///
///   The factory walks up from the current working directory until it finds
///   an <c>appsettings.json</c> file — defensive against the various bin
///   output layouts (<c>bin/Debug/net10.0/</c>, publish output, etc.).
///
///   If you ever change the startup project layout, the walk-up logic still
///   works. The fallback (env var <c>TakOne__Database__ConnectionString</c>)
///   is also supported so CI pipelines can override it without touching
///   appsettings.json. User secrets (from the WebUI project's
///   <c>&lt;UserSecretsId&gt;</c>) are also loaded so a developer's real
///   Dev connection string — stored in user secrets so it never enters
///   source control — is honored by EF tooling too.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    /// <inheritdoc />
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        // ----------------------------------------------------------------
        // 1. Build an IConfiguration that mirrors what the real app uses in
        //    Development: appsettings.json + user secrets + env vars.
        //
        //    The WebUI project is the startup project passed via
        //    --startup-project, so its appsettings.json is the canonical
        //    base source of the connection string. User secrets are loaded
        //    so that `dotnet ef database update` uses the SAME connection
        //    string the running app uses in Development (the real DB
        //    server, not the placeholder `Server=localhost;` shipped in
        //    appsettings.json).
        //
        //    NOTE on appsettings.Development.json: deliberately NOT loaded
        //    by the factory, even though the real app loads it in Dev. The
        //    factory only needs the connection string, which lives in
        //    appsettings.json — and `AddJsonFile(name, optional: true)`
        //    only suppresses the error when the file is MISSING, not when
        //    it exists but is malformed (empty file, UTF-16 BOM saved by
        //    Visual Studio, accidentally truncated, etc.). Design-time
        //    tooling should not fail because of a broken Dev-only config
        //    file, so we skip it.
        //
        //    NOTE on user secrets: the <UserSecretsId> element lives in
        //    TakOne.WebUI.csproj (NOT in this project), so we can't use
        //    the generic `AddUserSecrets<T>()` overload — T would need to
        //    be a type from the WebUI assembly, which we can't reference
        //    from Infrastructure (circular dependency). Instead, we find
        //    the WebUI assembly at runtime (EF tooling has already loaded
        //    it because it's the --startup-project) and pass it to the
        //    `AddUserSecrets(Assembly)` overload. That overload looks up
        //    the AssemblyUserSecretsIdAttribute on the assembly — the
        //    attribute that the <UserSecretsId> MSBuild property emits at
        //    build time — and uses its ID to locate the secrets file
        //    (e.g. %APPDATA%\Microsoft\UserSecrets\<id>\secrets.json on
        //    Windows). If the assembly can't be found (e.g. running the
        //    factory from a non-WebUI startup project), we silently skip
        //    user secrets — the env var fallback still applies.
        // ----------------------------------------------------------------
        var webUiProjectRoot = FindWebUiProjectRoot();
        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(webUiProjectRoot)
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables();

        // Try to load the WebUI assembly's user secrets. Best-effort:
        // if the assembly isn't loaded, skip — the user can still set
        // the env var TakOne__Database__ConnectionString.
        var webUiAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "TakOne.WebUI");
        if (webUiAssembly is not null)
        {
            configBuilder.AddUserSecrets(webUiAssembly);
        }

        var configuration = configBuilder.Build();

        // ----------------------------------------------------------------
        // 2. Read the connection string the same way AddTakOneInfrastructure
        //    does — from the "TakOne:Database" section. This keeps the
        //    factory and the real DI registration in sync (a single source
        //    of truth for the key name).
        // ----------------------------------------------------------------
        var connectionString = configuration
            .GetSection(TakOneDatabaseOptions.SectionName)
            .GetValue<string>(nameof(TakOneDatabaseOptions.ConnectionString));

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Fallback: also try the legacy ConnectionStrings:DefaultConnection
            // key (the one EF's own tooling would look for by convention).
            connectionString = configuration
                .GetConnectionString("DefaultConnection");
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"The TakOne database connection string could not be found " +
                $"for design-time EF tooling. Looked under " +
                $"'{TakOneDatabaseOptions.SectionName}:ConnectionString' in " +
                $"'{Path.Combine(webUiProjectRoot, "appsettings.json")}', " +
                $"in the TakOne.WebUI user secrets " +
                $"(UserSecretsId cb14d953-0abd-4c19-9089-e4566a8d4717), " +
                $"and under 'ConnectionStrings:DefaultConnection'. " +
                $"Set one of these, or set the env var " +
                $"'TakOne__Database__ConnectionString'.");
        }

        // ----------------------------------------------------------------
        // 3. Build the DbContextOptions the same way AddTakOneInfrastructure
        //    does — UseSqlServer with the connection string. No interceptors,
        //    no Wolverine — we only need the schema, not the runtime behavior.
        // ----------------------------------------------------------------
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        optionsBuilder.UseSqlServer(connectionString);

        // ----------------------------------------------------------------
        // 4. Construct and return the DbContext. EF tooling uses this
        //    instance to call OnModelCreating + snapshot the model + emit
        //    the migration code.
        // ----------------------------------------------------------------
        return new ApplicationDbContext(optionsBuilder.Options);
    }

    /// <summary>
    /// Locates a directory containing <c>appsettings.json</c> starting from
    /// the current working directory of the EF tooling process and walking
    /// upward.
    ///
    /// EF tooling typically runs from the startup project's
    /// <c>bin/&lt;config&gt;/&lt;tfm&gt;</c> directory (e.g.
    /// <c>TakOne.WebUI/bin/Debug/net10.0/</c>). Going up two levels gets us
    /// back to the WebUI project root, where <c>appsettings.json</c> lives.
    /// We verify by looking for <c>appsettings.json</c> there; if it's
    /// missing, we keep walking up the directory tree until we find it
    /// (defensive — handles publish output and other layouts).
    /// </summary>
    private static string FindWebUiProjectRoot()
    {
        var currentDir = Environment.CurrentDirectory;

        var dir = new DirectoryInfo(currentDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "appsettings.json")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        // Fallback: assume the conventional bin/Debug/net10.0/ layout.
        // Going up two levels from there lands in the project root.
        return Path.GetFullPath(
            Path.Combine(currentDir, "..", ".."));
    }
}