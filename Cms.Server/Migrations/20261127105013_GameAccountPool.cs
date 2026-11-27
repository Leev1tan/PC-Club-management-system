using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cms.Server.Migrations
{
    /// <inheritdoc />
    public partial class GameAccountPool : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccountPlatform",
                table: "Games",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GameAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LoginMask = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    LoginCipher = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    SecretCipher = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SecretUpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RemovedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedByUsername = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GameAccountLeases",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    GameId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeliveredUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndReason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    EndedByUsername = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameAccountLeases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameAccountLeases_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GameAccountLeases_GameAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "GameAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameAccountLeases_AccountId_Open",
                table: "GameAccountLeases",
                column: "AccountId",
                unique: true,
                filter: "\"EndedUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GameAccountLeases_DeviceId_Platform_Open",
                table: "GameAccountLeases",
                columns: new[] { "DeviceId", "Platform" },
                unique: true,
                filter: "\"EndedUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GameAccountLeases_SessionId",
                table: "GameAccountLeases",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_GameAccounts_Platform",
                table: "GameAccounts",
                column: "Platform");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameAccountLeases");

            migrationBuilder.DropTable(
                name: "GameAccounts");

            migrationBuilder.DropColumn(
                name: "AccountPlatform",
                table: "Games");
        }
    }
}
