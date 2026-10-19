using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeviceTelemetry.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotificationLanguages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Templates_StatusCode",
                table: "Templates");

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "Templates",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectedNotifyLanguage",
                table: "TelemetryEvents",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotifyLanguage",
                table: "Devices",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "Customers",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Templates_StatusCode_Language",
                table: "Templates",
                columns: new[] { "StatusCode", "Language" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Templates_StatusCode_Language",
                table: "Templates");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "Templates");

            migrationBuilder.DropColumn(
                name: "RejectedNotifyLanguage",
                table: "TelemetryEvents");

            migrationBuilder.DropColumn(
                name: "NotifyLanguage",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "Customers");

            migrationBuilder.CreateIndex(
                name: "IX_Templates_StatusCode",
                table: "Templates",
                column: "StatusCode",
                unique: true);
        }
    }
}
