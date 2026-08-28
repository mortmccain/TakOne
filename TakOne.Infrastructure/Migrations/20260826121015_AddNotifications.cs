using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TakOne.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNotifications : Migration
    {
        // CA1861: extract constant column-name arrays to private static readonly
        // fields so they are allocated once per type load (not once per
        // migration apply call). Migrations run at most once per deployment,
        // so the allocation saving is negligible — but the analyzer still
        // fires because the migration apply methods are syntactically called
        // repeatedly, so we centralize the arrays here to satisfy it cleanly
        // rather than suppress the rule.
        private static readonly string[] s_ixNotificationsUserIdCreatedAtUtc =
            { "UserId", "CreatedAtUtc" };
        private static readonly string[] s_ixNotificationsUserIdReadAtUtcUnread =
            { "UserId", "ReadAtUtc" };
        private static readonly string[] s_uxNotificationsUserIdSaleIdKind =
            { "UserId", "SaleId", "Kind" };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    SaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SaleDisplayNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ActorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReadAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_CreatedAtUtc",
                table: "Notifications",
                columns: s_ixNotificationsUserIdCreatedAtUtc);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_ReadAtUtc_Unread",
                table: "Notifications",
                columns: s_ixNotificationsUserIdReadAtUtcUnread,
                filter: "[ReadAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Notifications_UserId_SaleId_Kind",
                table: "Notifications",
                columns: s_uxNotificationsUserIdSaleIdKind,
                unique: true,
                filter: "[SaleId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Notifications");
        }
    }
}
