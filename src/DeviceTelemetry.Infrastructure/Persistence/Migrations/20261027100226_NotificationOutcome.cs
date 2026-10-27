using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeviceTelemetry.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotificationOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Notification",
                table: "TelemetryEvents",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotificationLanguage",
                table: "TelemetryEvents",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotificationSubject",
                table: "TelemetryEvents",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryEvents_DeviceId_OccurredAtUtc_Id_Notified",
                table: "TelemetryEvents",
                columns: new[] { "DeviceId", "OccurredAtUtc", "Id" },
                descending: new[] { false, true, true },
                filter: "\"Notification\" = 'Queued'");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_TelemetryEventId",
                table: "OutboxMessages",
                column: "TelemetryEventId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TelemetryEvents_DeviceId_OccurredAtUtc_Id_Notified",
                table: "TelemetryEvents");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_TelemetryEventId",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "Notification",
                table: "TelemetryEvents");

            migrationBuilder.DropColumn(
                name: "NotificationLanguage",
                table: "TelemetryEvents");

            migrationBuilder.DropColumn(
                name: "NotificationSubject",
                table: "TelemetryEvents");
        }
    }
}
