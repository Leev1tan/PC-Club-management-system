using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cms.Server.Migrations
{
    /// <inheritdoc />
    public partial class Billing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing tariffs bill per minute (the code also reads 0 as 1)
            migrationBuilder.AddColumn<int>(
                name: "BillingStepMinutes",
                table: "Tariffs",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<decimal>(
                name: "MinimumCharge",
                table: "Tariffs",
                type: "numeric(10,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CutoffUtc",
                table: "Sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PackageId",
                table: "Sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PaidMinutes",
                table: "Sessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PausedUtc",
                table: "Sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentMethod",
                table: "Sessions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LedgerEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    BalanceDelta = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    BonusDelta = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    DeskAmount = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    BalanceAfter = table.Column<decimal>(type: "numeric(10,2)", nullable: true),
                    BonusAfter = table.Column<decimal>(type: "numeric(10,2)", nullable: true),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    PackageId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorUsername = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LedgerEntries_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Append-only: the database refuses UPDATE and DELETE on the money ledger
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION ccms_ledger_append_only() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'LedgerEntries is append-only';
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER ledger_entries_append_only BEFORE UPDATE OR DELETE ON ""LedgerEntries""
    FOR EACH ROW EXECUTE PROCEDURE ccms_ledger_append_only();");

            migrationBuilder.CreateTable(
                name: "Packages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ZoneId = table.Column<Guid>(type: "uuid", nullable: true),
                    Price = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: true),
                    WindowStartMinute = table.Column<int>(type: "integer", nullable: true),
                    WindowEndMinute = table.Column<int>(type: "integer", nullable: true),
                    DaysMask = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Packages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Packages_Zones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "Zones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SessionSegments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TariffId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionSegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionSegments_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TariffRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TariffId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DaysMask = table.Column<int>(type: "integer", nullable: false),
                    StartMinute = table.Column<int>(type: "integer", nullable: false),
                    EndMinute = table.Column<int>(type: "integer", nullable: false),
                    PricePerHour = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TariffRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TariffRules_Tariffs_TariffId",
                        column: x => x.TariffId,
                        principalTable: "Tariffs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_PackageId",
                table: "Sessions",
                column: "PackageId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_CreatedUtc",
                table: "LedgerEntries",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_SessionId",
                table: "LedgerEntries",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_UserId_Id",
                table: "LedgerEntries",
                columns: new[] { "UserId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Packages_ZoneId",
                table: "Packages",
                column: "ZoneId");

            migrationBuilder.CreateIndex(
                name: "IX_SessionSegments_SessionId",
                table: "SessionSegments",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_TariffRules_TariffId_SortOrder",
                table: "TariffRules",
                columns: new[] { "TariffId", "SortOrder" });

            migrationBuilder.AddForeignKey(
                name: "FK_Sessions_Packages_PackageId",
                table: "Sessions",
                column: "PackageId",
                principalTable: "Packages",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Sessions_Packages_PackageId",
                table: "Sessions");

            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS ledger_entries_append_only ON ""LedgerEntries""; DROP FUNCTION IF EXISTS ccms_ledger_append_only();");

            migrationBuilder.DropTable(
                name: "LedgerEntries");

            migrationBuilder.DropTable(
                name: "Packages");

            migrationBuilder.DropTable(
                name: "SessionSegments");

            migrationBuilder.DropTable(
                name: "TariffRules");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_PackageId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "BillingStepMinutes",
                table: "Tariffs");

            migrationBuilder.DropColumn(
                name: "MinimumCharge",
                table: "Tariffs");

            migrationBuilder.DropColumn(
                name: "CutoffUtc",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "PackageId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "PaidMinutes",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "PausedUtc",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "PaymentMethod",
                table: "Sessions");
        }
    }
}
