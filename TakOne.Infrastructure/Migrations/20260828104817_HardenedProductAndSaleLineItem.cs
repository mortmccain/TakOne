using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TakOne.Infrastructure.Migrations
{
    /// <summary>
    /// Hardens the data-integrity invariants on the Products + SaleLineItems
    /// tables. Closes v4 brutal-review High-severity findings #03 (SaleLineItem
    /// SaleId nullable) and #04 (Product.Name non-unique index), plus
    /// Medium-severity finding #22 (Product has no IsActive / soft-delete).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>PRE-FLIGHT — ORPHAN CLEANUP (SaleLineItem.SaleId):</b> The
    /// <c>ALTER COLUMN SaleId uniqueidentifier NOT NULL</c> below will fail
    /// if any <c>SaleLineItems</c> row has <c>SaleId IS NULL</c>. The domain
    /// invariant says no such row should exist (a SaleLineItem cannot exist
    /// without a parent Sale), but defensive data-audit must run first to
    /// clean up any historical orphans BEFORE the ALTER. We use
    /// <c>DELETE FROM SaleLineItems WHERE SaleId IS NULL</c> rather than
    /// attempting to attach orphans to a synthetic parent — an orphaned
    /// line item is by definition semantically invalid (it has no customer,
    /// no audit trail, no purchase context); deleting it is the only
    /// honest option. The IF EXISTS guard makes this idempotent.
    /// </para>
    /// <para>
    /// <b>PRE-FLIGHT — DUPLICATE PRODUCT NAMES:</b> The
    /// <c>CREATE UNIQUE INDEX IX_Products_Name</c> below will fail if any
    /// duplicate Product.Name values exist. The application-layer
    /// <c>NameExistsAsync</c> check should have prevented this, but races
    /// could have produced duplicates. The migration does NOT auto-dedupe
    /// — that's a destructive operation that requires the operator's
    /// judgment (which duplicate to keep, which to rename, which to delete).
    /// If duplicates exist, this migration will fail loudly at
    /// <c>CREATE INDEX</c> time, surfacing the data-quality issue.
    /// </para>
    /// <para>
    /// <b>NEW COLUMN — Products.IsActive:</b> Existing rows default to
    /// <c>IsActive = 1</c> (true). This matches the pre-migration behavior
    /// where "active" was implicit (no flag). Previously-soft-deleted
    /// products (those with <c>StockQuantity = 0</c> from the old
    /// <c>DeactivateProductCommandHandler</c> calling
    /// <c>SetStock(0)</c>) will become <c>IsActive = 1</c> after this
    /// migration — they are technically "active" under the new model
    /// but with 0 stock. Operators should run a one-time audit query
    /// (<c>SELECT * FROM Products WHERE StockQuantity = 0</c>) and
    /// explicitly <c>Deactivate()</c> any that should remain inactive.
    /// </para>
    /// </remarks>
    public partial class HardenedProductAndSaleLineItem : Migration
    {
        // CA1861: extract the constant column-name array (used in both the Up
        // and Down migrations on the same index) to a private static readonly
        // field so it's allocated once per type load rather than once per call.
        private static readonly string[] s_ixSaleLineItemsSaleIdLineNumber =
            { "SaleId", "LineNumber" };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ------------------------------------------------------------------
            // PRE-FLIGHT: delete any orphaned SaleLineItems with NULL SaleId.
            // The ALTER COLUMN below requires NOT NULL — orphans block the
            // migration. Deleting them is the honest option (see class doc).
            // The DELETE is wrapped in a WHERE so it's a no-op if there are
            // no orphans (the common case).
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"
-- v4 HardenedProductAndSaleLineItem: orphan cleanup.
-- A SaleLineItem with NULL SaleId is semantically invalid (no parent
-- Sale = no customer = no audit context). Delete before ALTER COLUMN.
DELETE FROM SaleLineItems WHERE SaleId IS NULL;");

            // ------------------------------------------------------------------
            // Drop the filtered unique index on (SaleId, LineNumber). The
            // filter "[SaleId] IS NOT NULL" was needed because SaleId was
            // nullable; once SaleId is NOT NULL, the filter is dead weight
            // and we recreate the index without it (next step).
            // ------------------------------------------------------------------
            migrationBuilder.DropIndex(
                name: "IX_SaleLineItems_SaleId_LineNumber",
                table: "SaleLineItems");

            // ------------------------------------------------------------------
            // Drop the non-unique IX_Products_Name. We recreate it as UNIQUE
            // (next step) to close the duplicate-product-name race condition.
            // ------------------------------------------------------------------
            migrationBuilder.DropIndex(
                name: "IX_Products_Name",
                table: "Products");

            // ------------------------------------------------------------------
            // ALTER SaleLineItem.SaleId from nullable to NOT NULL. The
            // orphan-cleanup SQL above ensures no NULL values remain.
            // The defaultValue is Guid.Empty for forward-compat with future
            // INSERT statements that omit the column (EF Core always
            // populates SaleId from the parent Sale's Id when saving a new
            // SaleLineItem via the shadow FK, so this default is a
            // safety-net only — never actually used).
            // ------------------------------------------------------------------
            migrationBuilder.AlterColumn<Guid>(
                name: "SaleId",
                table: "SaleLineItems",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            // ------------------------------------------------------------------
            // Add Products.IsActive (soft-delete flag). Default = true for
            // all existing rows (matches the pre-migration behavior where
            // active was implicit). See class doc for the post-migration
            // audit recommendation.
            // ------------------------------------------------------------------
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Products",
                type: "bit",
                nullable: false,
                defaultValue: true);

            // ------------------------------------------------------------------
            // Recreate the (SaleId, LineNumber) unique index WITHOUT the
            // [SaleId] IS NOT NULL filter. The column is now NOT NULL, so
            // the filter is redundant — and a non-filtered unique index is
            // slightly faster and cleaner.
            // ------------------------------------------------------------------
            migrationBuilder.CreateIndex(
                name: "IX_SaleLineItems_SaleId_LineNumber",
                table: "SaleLineItems",
                columns: s_ixSaleLineItemsSaleIdLineNumber,
                unique: true);

            // ------------------------------------------------------------------
            // Recreate IX_Products_Name as UNIQUE. Closes the
            // duplicate-product-name race condition where two concurrent
            // CreateProduct commands both passed the NameExistsAsync check
            // and both INSERTed. With the unique constraint, the second
            // INSERT now fails at the DB level (translated to a friendly
            // error by the application layer).
            // ------------------------------------------------------------------
            migrationBuilder.CreateIndex(
                name: "IX_Products_Name",
                table: "Products",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // NOTE: the Down migration is best-effort. OnceIsActive is added
            // and SaleLineItem.SaleId is NOT NULL, reverting requires:
            //   1. Restoring the IX_Products_Name as non-unique.
            //   2. Restoring the (SaleId, LineNumber) filtered unique index.
            //   3. Dropping the IsActive column.
            //   4. Reverting SaleId to nullable.
            // The data loss in step 3 (IsActive column is dropped, all
            // soft-deleted products revert to "implicitly active") is the
            // reason this migration is one-way in practice.

            migrationBuilder.DropIndex(
                name: "IX_SaleLineItems_SaleId_LineNumber",
                table: "SaleLineItems");

            migrationBuilder.DropIndex(
                name: "IX_Products_Name",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Products");

            migrationBuilder.AlterColumn<Guid>(
                name: "SaleId",
                table: "SaleLineItems",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.CreateIndex(
                name: "IX_SaleLineItems_SaleId_LineNumber",
                table: "SaleLineItems",
                columns: s_ixSaleLineItemsSaleIdLineNumber,
                unique: true,
                filter: "[SaleId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Name",
                table: "Products",
                column: "Name");
        }
    }
}
