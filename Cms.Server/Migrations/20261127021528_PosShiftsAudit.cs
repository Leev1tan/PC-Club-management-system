using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cms.Server.Migrations
{
    /// <inheritdoc />
    public partial class PosShiftsAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Debt",
                table: "Users",
                type: "numeric(10,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "DebtAfter",
                table: "LedgerEntries",
                type: "numeric(10,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DebtDelta",
                table: "LedgerEntries",
                type: "numeric(10,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<long>(
                name: "SaleId",
                table: "LedgerEntries",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ShiftId",
                table: "LedgerEntries",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Hash",
                table: "AuditLog",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrevHash",
                table: "AuditLog",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Price = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    TrackStock = table.Column<bool>(type: "boolean", nullable: false),
                    StockQuantity = table.Column<int>(type: "integer", nullable: false),
                    LowStockThreshold = table.Column<int>(type: "integer", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sales",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RefundOfSaleId = table.Column<long>(type: "bigint", nullable: true),
                    ShiftId = table.Column<long>(type: "bigint", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Total = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorUsername = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sales", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sales_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Shifts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OpenedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OpenedById = table.Column<Guid>(type: "uuid", nullable: true),
                    OpenedByUsername = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OpeningFloat = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    OpenNote = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ClosedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClosedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ClosedByUsername = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CountedCash = table.Column<decimal>(type: "numeric(10,2)", nullable: true),
                    ExpectedCash = table.Column<decimal>(type: "numeric(10,2)", nullable: true),
                    Variance = table.Column<decimal>(type: "numeric(10,2)", nullable: true),
                    CloseNote = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    AuditHeadId = table.Column<long>(type: "bigint", nullable: true),
                    AuditHeadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shifts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockMovements",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Delta = table.Column<int>(type: "integer", nullable: false),
                    QuantityAfter = table.Column<int>(type: "integer", nullable: false),
                    SaleId = table.Column<long>(type: "bigint", nullable: true),
                    ShiftId = table.Column<long>(type: "bigint", nullable: true),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    UnitCost = table.Column<decimal>(type: "numeric(10,2)", nullable: true),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorUsername = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockMovements_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SaleItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SaleId = table.Column<long>(type: "bigint", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    LineTotal = table.Column<decimal>(type: "numeric(10,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaleItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SaleItems_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleItems_Sales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "Sales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_SaleId",
                table: "LedgerEntries",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_ShiftId",
                table: "LedgerEntries",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Category_SortOrder",
                table: "Products",
                columns: new[] { "Category", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_SaleItems_ProductId",
                table: "SaleItems",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleItems_SaleId",
                table: "SaleItems",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_Sales_RefundOfSaleId",
                table: "Sales",
                column: "RefundOfSaleId",
                unique: true,
                filter: "\"RefundOfSaleId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Sales_ShiftId",
                table: "Sales",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_Sales_UserId",
                table: "Sales",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Shifts_Open",
                table: "Shifts",
                column: "Status",
                unique: true,
                filter: "\"Status\" = 'open'");

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_ProductId_Id",
                table: "StockMovements",
                columns: new[] { "ProductId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_ShiftId",
                table: "StockMovements",
                column: "ShiftId");

            // Receipts and the stock log are append-only; a closed shift cannot be changed or deleted
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION ccms_append_only() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION '% is append-only', TG_TABLE_NAME;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER sales_append_only BEFORE UPDATE OR DELETE ON ""Sales""
    FOR EACH ROW EXECUTE PROCEDURE ccms_append_only();
CREATE TRIGGER sale_items_append_only BEFORE UPDATE OR DELETE ON ""SaleItems""
    FOR EACH ROW EXECUTE PROCEDURE ccms_append_only();
CREATE TRIGGER stock_movements_append_only BEFORE UPDATE OR DELETE ON ""StockMovements""
    FOR EACH ROW EXECUTE PROCEDURE ccms_append_only();
CREATE OR REPLACE FUNCTION ccms_shift_guard() RETURNS trigger AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Shifts cannot be deleted';
    END IF;
    IF OLD.""Status"" = 'closed' THEN
        RAISE EXCEPTION 'A closed shift cannot be changed';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER shifts_guard BEFORE UPDATE OR DELETE ON ""Shifts""
    FOR EACH ROW EXECUTE PROCEDURE ccms_shift_guard();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS sales_append_only ON ""Sales"";
DROP TRIGGER IF EXISTS sale_items_append_only ON ""SaleItems"";
DROP TRIGGER IF EXISTS stock_movements_append_only ON ""StockMovements"";
DROP TRIGGER IF EXISTS shifts_guard ON ""Shifts"";
DROP FUNCTION IF EXISTS ccms_append_only();
DROP FUNCTION IF EXISTS ccms_shift_guard();");

            migrationBuilder.DropTable(
                name: "SaleItems");

            migrationBuilder.DropTable(
                name: "Shifts");

            migrationBuilder.DropTable(
                name: "StockMovements");

            migrationBuilder.DropTable(
                name: "Sales");

            migrationBuilder.DropTable(
                name: "Products");

            migrationBuilder.DropIndex(
                name: "IX_LedgerEntries_SaleId",
                table: "LedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_LedgerEntries_ShiftId",
                table: "LedgerEntries");

            migrationBuilder.DropColumn(
                name: "Debt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DebtAfter",
                table: "LedgerEntries");

            migrationBuilder.DropColumn(
                name: "DebtDelta",
                table: "LedgerEntries");

            migrationBuilder.DropColumn(
                name: "SaleId",
                table: "LedgerEntries");

            migrationBuilder.DropColumn(
                name: "ShiftId",
                table: "LedgerEntries");

            migrationBuilder.DropColumn(
                name: "Hash",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "PrevHash",
                table: "AuditLog");
        }
    }
}
