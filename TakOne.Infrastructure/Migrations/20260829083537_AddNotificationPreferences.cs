using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TakOne.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Round 3 — per-user, per-kind notification mute preferences
    /// (the Settings page's notification-preferences card).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>WHY THIS TABLE</b>: mutes are enforced at notification CREATION
    /// time (the NotifyOn* event handlers + BroadcastFanout consult
    /// <c>INotificationPreferenceRepository.IsMutedAsync</c> /
    /// <c>GetMutedUserIdsAsync</c> before inserting Notification rows).
    /// Sparse storage: a (UserId, Kind) row exists ONLY after the user's
    /// first mute of that kind; un-muting flips IsMuted to false and KEEPS
    /// the row (durable explicit choice, cheap re-toggle).
    /// </para>
    /// <para>
    /// <b>NOTE ON THE ROWVERSION SENTINEL</b>: EF Core 10 records
    /// <c>HasDefaultValue(new byte[0])</c> for rowversion concurrency tokens
    /// in the model snapshot (a sentinel-value behavior change). The
    /// scaffolder emitted no-op <c>AlterColumn</c> operations for the 8
    /// PRE-EXISTING RowVersion columns as a result — they were removed by
    /// hand because <c>ALTER COLUMN ... rowversion</c> is not valid T-SQL
    /// (SQL Server rejects altering an existing column TO rowversion) and
    /// the column definitions did not actually change. Only the new table
    /// + its unique index remain, matching the pattern of every prior
    /// migration in this project.
    /// </para>
    /// </remarks>
    public partial class AddNotificationPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationPreferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    IsMuted = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false, defaultValue: new byte[0])
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationPreferences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_NotificationPreferences_UserId_Kind",
                table: "NotificationPreferences",
                columns: new[] { "UserId", "Kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationPreferences");
        }
    }
}
