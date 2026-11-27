using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cms.Server.Migrations
{
    /// <inheritdoc />
    public partial class BarOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "BarOrderId",
                table: "LedgerEntries",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BarOrders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Payment = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Total = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeliveredUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SaleId = table.Column<long>(type: "bigint", nullable: true),
                    DeskMethod = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    CancelReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    HandledByUsername = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BarOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BarOrders_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BarOrders_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "BarOrderItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrderId = table.Column<long>(type: "bigint", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    LineTotal = table.Column<decimal>(type: "numeric(10,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BarOrderItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BarOrderItems_BarOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "BarOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BarOrderItems_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_BarOrderId",
                table: "LedgerEntries",
                column: "BarOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_BarOrderItems_OrderId",
                table: "BarOrderItems",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_BarOrderItems_ProductId",
                table: "BarOrderItems",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_BarOrders_DeviceId_Open",
                table: "BarOrders",
                column: "DeviceId",
                unique: true,
                filter: "\"Status\" IN ('new', 'preparing')");

            migrationBuilder.CreateIndex(
                name: "IX_BarOrders_Status_Id",
                table: "BarOrders",
                columns: new[] { "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_BarOrders_UserId",
                table: "BarOrders",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BarOrderItems");

            migrationBuilder.DropTable(
                name: "BarOrders");

            migrationBuilder.DropIndex(
                name: "IX_LedgerEntries_BarOrderId",
                table: "LedgerEntries");

            migrationBuilder.DropColumn(
                name: "BarOrderId",
                table: "LedgerEntries");
        }
    }
}
