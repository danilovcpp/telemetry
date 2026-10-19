using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeviceTelemetry.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeviceSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Channels",
                table: "Devices",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MonitoringEnabled",
                table: "Devices",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OfflineAfterS",
                table: "Devices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeZone",
                table: "Devices",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Channels",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "MonitoringEnabled",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "OfflineAfterS",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "TimeZone",
                table: "Devices");
        }
    }
}
