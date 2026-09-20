// MigrationFixer — pre-efbundle tool that marks existing migrations as applied.
//
// PROBLEM:
//   The efbundle runs `CREATE TABLE [AspNetRoles] ...` but the table already
//   exists — the database was created by a previous deploy that either called
//   Database.Migrate() directly or used a different efbundle version. The
//   __EFMigrationsHistory table is either missing or doesn't have the
//   InitialCreate entry, so the efbundle thinks the migration hasn't run
//   and tries to create tables that already exist → SQL error 2714
//   "There is already an object named 'AspNetRoles' in the database."
//
// FIX:
//   Before running the efbundle, this tool:
//     1. Connects to the DB.
//     2. Creates __EFMigrationsHistory if it doesn't exist.
//     3. Checks if AspNetRoles exists (meaning the schema is already applied).
//     4. If the schema exists AND the migration isn't in __EFMigrationsHistory,
//        inserts the migration record — marking it as "already applied".
//     5. The efbundle then sees the migration as applied and skips it,
//        running only genuinely new migrations (if any).
//
// USAGE:
//   dotnet TakOne.MigrationFixer.dll "<connection-string>"

using Microsoft.Data.SqlClient;

var connectionString = args.Length > 0 ? args[0] : throw new ArgumentException("Connection string required as first argument.");

// All known migrations in the project. When new migrations are added,
// append them here. The tool will mark each as applied IF the schema
// already exists (AspNetRoles is the proxy table — if it exists, the
// initial migration was applied).
var knownMigrations = new[]
{
    ("20260829095735_InitialCreate", "10.0.12"),
};

try
{
    using var conn = new SqlConnection(connectionString);
    conn.Open();
    Console.WriteLine("[MigrationFixer] Connected to database.");

    // 1. Ensure __EFMigrationsHistory exists.
    EnsureMigrationsHistoryTable(conn);

    // 2. Check if the schema is already applied (AspNetRoles is the first
    //    table the InitialCreate migration creates — it's a reliable proxy).
    if (TableExists(conn, "AspNetRoles"))
    {
        Console.WriteLine("[MigrationFixer] AspNetRoles table exists — schema is already applied.");
        Console.WriteLine("[MigrationFixer] Marking known migrations as applied in __EFMigrationsHistory...");

        foreach (var (migrationId, productVersion) in knownMigrations)
        {
            if (!MigrationIsRecorded(conn, migrationId))
            {
                InsertMigrationRecord(conn, migrationId, productVersion);
                Console.WriteLine($"[MigrationFixer]   Inserted: {migrationId}");
            }
            else
            {
                Console.WriteLine($"[MigrationFixer]   Already recorded: {migrationId}");
            }
        }
        Console.WriteLine("[MigrationFixer] Done — efbundle will skip these migrations.");
    }
    else
    {
        Console.WriteLine("[MigrationFixer] AspNetRoles does NOT exist — fresh database. efbundle will create the schema.");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[MigrationFixer] WARNING: {ex.Message}");
    Console.WriteLine("[MigrationFixer] Continuing anyway — the efbundle will retry.");
    // Exit 0 — don't block the efbundle. If the DB is unreachable, the
    // efbundle will also fail and the entrypoint's error handling kicks in.
    return;
}

return;

static void EnsureMigrationsHistoryTable(SqlConnection conn)
{
    var cmd = new SqlCommand(@"
        IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[__EFMigrationsHistory]') AND type in (N'U'))
        BEGIN
            CREATE TABLE [dbo].[__EFMigrationsHistory] (
                [MigrationId] nvarchar(150) NOT NULL,
                [ProductVersion] nvarchar(32) NOT NULL,
                CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
            );
        END
    ", conn);
    cmd.ExecuteNonQuery();
}

static bool TableExists(SqlConnection conn, string tableName)
{
    var cmd = new SqlCommand(@"
        SELECT COUNT(*) FROM information_schema.tables WHERE table_name = @tableName
    ", conn);
    cmd.Parameters.AddWithValue("@tableName", tableName);
    return (int)cmd.ExecuteScalar()! > 0;
}

static bool MigrationIsRecorded(SqlConnection conn, string migrationId)
{
    var cmd = new SqlCommand(@"
        SELECT COUNT(*) FROM [dbo].[__EFMigrationsHistory] WHERE [MigrationId] = @migrationId
    ", conn);
    cmd.Parameters.AddWithValue("@migrationId", migrationId);
    return (int)cmd.ExecuteScalar()! > 0;
}

static void InsertMigrationRecord(SqlConnection conn, string migrationId, string productVersion)
{
    var cmd = new SqlCommand(@"
        INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
        VALUES (@migrationId, @productVersion)
    ", conn);
    cmd.Parameters.AddWithValue("@migrationId", migrationId);
    cmd.Parameters.AddWithValue("@productVersion", productVersion);
    cmd.ExecuteNonQuery();
}
