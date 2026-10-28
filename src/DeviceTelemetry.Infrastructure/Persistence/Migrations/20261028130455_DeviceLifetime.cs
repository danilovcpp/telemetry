using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DeviceTelemetry.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeviceLifetime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Lifetime",
                table: "Devices",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LifetimeAtUtc",
                table: "Devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DeviceLifetimeSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Lifetime = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceLifetimeSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceLifetimeSnapshots_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceLifetimeSnapshots_DeviceId_OccurredAtUtc_EventId",
                table: "DeviceLifetimeSnapshots",
                columns: new[] { "DeviceId", "OccurredAtUtc", "EventId" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceLifetimeSnapshots_EventId",
                table: "DeviceLifetimeSnapshots",
                column: "EventId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceLifetimeSnapshots");

            migrationBuilder.DropColumn(
                name: "Lifetime",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "LifetimeAtUtc",
                table: "Devices");
        }
    }
}
