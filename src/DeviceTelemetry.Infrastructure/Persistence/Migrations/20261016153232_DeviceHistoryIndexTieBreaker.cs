using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeviceTelemetry.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeviceHistoryIndexTieBreaker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TelemetryEvents_DeviceId_OccurredAtUtc",
                table: "TelemetryEvents");

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryEvents_DeviceId_OccurredAtUtc_Id",
                table: "TelemetryEvents",
                columns: new[] { "DeviceId", "OccurredAtUtc", "Id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TelemetryEvents_DeviceId_OccurredAtUtc_Id",
                table: "TelemetryEvents");

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryEvents_DeviceId_OccurredAtUtc",
                table: "TelemetryEvents",
                columns: new[] { "DeviceId", "OccurredAtUtc" },
                descending: new[] { false, true });
        }
    }
}
