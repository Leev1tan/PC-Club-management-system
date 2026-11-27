using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cms.Server.Migrations
{
    /// <inheritdoc />
    public partial class RetentionIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Heartbeats_CreatedUtc",
                table: "Heartbeats",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Commands_Status_CreatedUtc",
                table: "Commands",
                columns: new[] { "Status", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Heartbeats_CreatedUtc",
                table: "Heartbeats");

            migrationBuilder.DropIndex(
                name: "IX_Commands_Status_CreatedUtc",
                table: "Commands");
        }
    }
}
