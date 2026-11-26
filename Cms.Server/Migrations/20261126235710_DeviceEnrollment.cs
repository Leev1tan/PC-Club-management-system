using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cms.Server.Migrations
{
    /// <inheritdoc />
    public partial class DeviceEnrollment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RegisteredUtc",
                table: "Devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Devices",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "approved");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RegisteredUtc",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Devices");
        }
    }
}
