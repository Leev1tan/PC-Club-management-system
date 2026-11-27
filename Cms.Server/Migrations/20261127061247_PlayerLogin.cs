using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cms.Server.Migrations
{
    /// <inheritdoc />
    public partial class PlayerLogin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BalanceEndUtc",
                table: "Sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LimitToBalance",
                table: "Sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BalanceEndUtc",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "LimitToBalance",
                table: "Sessions");
        }
    }
}
