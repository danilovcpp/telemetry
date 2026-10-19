using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeviceTelemetry.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeviceLivenessAndEventMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Channels",
                table: "TelemetryEvents",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectedNotifyEmail",
                table: "TelemetryEvents",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RejectedOccurredAtUtc",
                table: "TelemetryEvents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RejectedReportIntervalS",
                table: "TelemetryEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "TelemetryEvents",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeenAtUtc",
                table: "Devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotifyEmail",
                table: "Devices",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReportIntervalS",
                table: "Devices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SilentSinceUtc",
                table: "Devices",
                type: "timestamp with time zone",
                nullable: true);

            // Existing devices: last contact = newest receive time among their (device-originated) events.
            migrationBuilder.Sql("""
                UPDATE "Devices" AS d
                SET "LastSeenAtUtc" = e."LastReceivedAtUtc"
                FROM (SELECT "DeviceId", MAX("ReceivedAtUtc") AS "LastReceivedAtUtc" FROM "TelemetryEvents" GROUP BY "DeviceId") AS e
                WHERE e."DeviceId" = d."Id";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryEvents_DeviceId_StatusCode_OccurredAtUtc_Id",
                table: "TelemetryEvents",
                columns: new[] { "DeviceId", "StatusCode", "OccurredAtUtc", "Id" },
                descending: new[] { false, false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TelemetryEvents_DeviceId_StatusCode_OccurredAtUtc_Id",
                table: "TelemetryEvents");

            migrationBuilder.DropColumn(
                name: "Channels",
                table: "TelemetryEvents");

            migrationBuilder.DropColumn(
                name: "RejectedNotifyEmail",
                table: "TelemetryEvents");

            migrationBuilder.DropColumn(
                name: "RejectedOccurredAtUtc",
                table: "TelemetryEvents");

            migrationBuilder.DropColumn(
                name: "RejectedReportIntervalS",
                table: "TelemetryEvents");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "TelemetryEvents");

            migrationBuilder.DropColumn(
                name: "LastSeenAtUtc",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "NotifyEmail",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "ReportIntervalS",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "SilentSinceUtc",
                table: "Devices");
        }
    }
}
