using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cms.Server.Migrations
{
    /// <inheritdoc />
    public partial class SessionConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ConcurrencyStamp",
                table: "Sessions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Earlier versions allowed two active sessions on one PC; keep only the newest one
            migrationBuilder.Sql(
                "UPDATE \"Sessions\" SET \"Status\" = 'ended', \"EndUtc\" = COALESCE(\"EndUtc\", now()) WHERE \"Status\" = 'active' AND EXISTS (" +
                "SELECT 1 FROM \"Sessions\" AS o WHERE o.\"DeviceId\" = \"Sessions\".\"DeviceId\" AND o.\"Status\" = 'active' " +
                "AND (o.\"StartUtc\" > \"Sessions\".\"StartUtc\" OR (o.\"StartUtc\" = \"Sessions\".\"StartUtc\" AND o.\"Id\" > \"Sessions\".\"Id\")))");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_DeviceId_Active",
                table: "Sessions",
                column: "DeviceId",
                unique: true,
                filter: "\"Status\" = 'active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sessions_DeviceId_Active",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ConcurrencyStamp",
                table: "Sessions");
        }
    }
}
