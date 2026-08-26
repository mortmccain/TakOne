using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TakOne.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBroadcastNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastKnownAppVersion",
                table: "SystemSettings",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BroadcastId",
                table: "Notifications",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Message",
                table: "Notifications",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "Notifications",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BroadcastNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SentByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    TargetRoleName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    TargetGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    FanoutKind = table.Column<int>(type: "int", nullable: false),
                    RecipientCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BroadcastNotifications", x => x.Id);
                });

            migrationBuilder.UpdateData(
                table: "SystemSettings",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000000"),
                column: "LastKnownAppVersion",
                value: null);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_BroadcastId",
                table: "Notifications",
                column: "BroadcastId",
                filter: "[BroadcastId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BroadcastNotifications_FanoutKind",
                table: "BroadcastNotifications",
                column: "FanoutKind");

            migrationBuilder.CreateIndex(
                name: "IX_BroadcastNotifications_SentAtUtc",
                table: "BroadcastNotifications",
                column: "SentAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BroadcastNotifications");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_BroadcastId",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "LastKnownAppVersion",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "BroadcastId",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "Message",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "Notifications");
        }
    }
}
