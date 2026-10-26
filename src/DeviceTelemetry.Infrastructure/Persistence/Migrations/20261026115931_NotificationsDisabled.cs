using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeviceTelemetry.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotificationsDisabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NotificationsDisabled",
                table: "Devices",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotificationsDisabled",
                table: "Devices");
        }
    }
}
