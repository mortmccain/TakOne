using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TakOne.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSaleSequenceCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SaleSequenceCounters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    NextSequence = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaleSequenceCounters", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SaleSequenceCounters_Year",
                table: "SaleSequenceCounters",
                column: "Year",
                unique: true);

            // ── BACKFILL ───────────────────────────────────────────────────
            // Seed one row per existing Persian year with
            // NextSequence = MAX(existing sequence in that year) + 1.
            // Without this, the first new sale of year 1405 would
            // get sequence 1 from the empty counter and collide with
            // an existing sale on the unique index.
            migrationBuilder.Sql(@"
INSERT INTO SaleSequenceCounters (Id, Year, NextSequence)
SELECT
    NEWID(),
    s.SaleNumber_Year,
    MAX(s.SaleNumber_Sequence) + 1
FROM Sales s
WHERE NOT EXISTS (
    SELECT 1 FROM SaleSequenceCounters c
    WHERE c.Year = s.SaleNumber_Year
)
GROUP BY s.SaleNumber_Year;
    ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SaleSequenceCounters");
        }
    }
}
