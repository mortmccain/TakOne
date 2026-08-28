using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TakOne.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// <b>NO-OP MIGRATION — INTENTIONAL (Brutal Code Review v3 #15, Round 18-C):</b>
    /// This migration has EMPTY Up and Down methods because the schema
    /// did not change. The change was MODEL-ONLY:
    /// <list type="bullet">
    ///   <item>The four Money-typed properties (<c>Product.Price</c>,
    ///   <c>SaleLineItem.UnitPrice</c>, <c>Sale.Total</c>,
    ///   <c>CustomerGroup.Salary</c>) were previously mapped as EF Core
    ///   OWNED ENTITIES via <c>OwnsOne</c>. They are now mapped as
    ///   EF Core 9+ COMPLEX PROPERTIES via <c>ComplexProperty</c>.</item>
    ///   <item><b>SCHEMA UNCHANGED:</b> both OwnsOne and ComplexProperty
    ///   flatten the value object's properties into the parent table
    ///   using the same column names (<c>{NavigationName}_{PropertyName}</c>
    ///   — e.g. <c>Price_Amount</c>, <c>Price_Currency</c>), the same
    ///   types (<c>decimal(18, 2)</c> + <c>nvarchar(3)</c>), and the
    ///   same nullability (NOT NULL).</item>
    ///   <item><b>BEHAVIORAL DIFFERENCE (in-memory, not SQL):</b>
    ///   ComplexProperty has VALUE SEMANTICS — EF Core compares complex
    ///   type instances by value (via
    ///   <c>BaseValueObject.GetEqualityComponents</c>), not by reference
    ///   identity. This eliminates the
    ///   <c>DbUpdateConcurrencyException</c> that the OwnsOne mapping
    ///   caused when a value object's reference was replaced (e.g.
    ///   <c>Sale.RecalculateTotal</c> does
    ///   <c>Total = sum + line.GrossTotal</c> — the + operator always
    ///   returns a new Money instance, which under OwnsOne confused
    ///   the change tracker).</item>
    /// </list>
    /// <para>
    /// The migration is KEPT (not deleted) because it serves as a
    /// historical marker: a future developer inspecting the migration
    /// history will see when the model switched from OwnsOne to
    /// ComplexProperty. The empty Up/Down methods are the correct
    /// representation of "the model changed but the schema didn't".
    /// </para>
    /// </remarks>
    public partial class ConvertMoneyToComplexType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty — see class-level XML doc.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty — see class-level XML doc.
        }
    }
}
