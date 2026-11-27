using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cms.Server.Migrations
{
    /// <inheritdoc />
    public partial class GameCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Games",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    LaunchKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ExePath = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: true),
                    Arguments = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    WorkingDirectory = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: true),
                    SteamAppId = table.Column<int>(type: "integer", nullable: true),
                    LaunchUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ProcessName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AllZones = table.Column<bool>(type: "boolean", nullable: false),
                    Color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Games", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GamePlays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndReason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GamePlays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GamePlays_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GamePlays_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GameZones",
                columns: table => new
                {
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameZones", x => new { x.GameId, x.ZoneId });
                    table.ForeignKey(
                        name: "FK_GameZones_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GameZones_Zones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "Zones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GamePlays_DeviceId_EndUtc",
                table: "GamePlays",
                columns: new[] { "DeviceId", "EndUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GamePlays_GameId",
                table: "GamePlays",
                column: "GameId");

            migrationBuilder.CreateIndex(
                name: "IX_GamePlays_StartUtc",
                table: "GamePlays",
                column: "StartUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Games_SortOrder_Name",
                table: "Games",
                columns: new[] { "SortOrder", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_GameZones_ZoneId",
                table: "GameZones",
                column: "ZoneId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GamePlays");

            migrationBuilder.DropTable(
                name: "GameZones");

            migrationBuilder.DropTable(
                name: "Games");
        }
    }
}
