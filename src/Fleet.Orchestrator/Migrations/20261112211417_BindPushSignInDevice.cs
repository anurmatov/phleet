using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleet.Orchestrator.Migrations
{
    /// <inheritdoc />
    public partial class BindPushSignInDevice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Old registrations have no trustworthy sign-in owner. Invalidate them instead of
            // letting a lost phone retain notifications after its new sign-in device is revoked.
            // Enrolled clients re-register their current token under their verified device.
            migrationBuilder.Sql("DELETE FROM push_devices;");

            migrationBuilder.AddColumn<string>(
                name: "AuthDeviceId",
                table: "push_devices",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_push_devices_AuthDeviceId",
                table: "push_devices",
                column: "AuthDeviceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_push_devices_AuthDeviceId",
                table: "push_devices");

            migrationBuilder.DropColumn(
                name: "AuthDeviceId",
                table: "push_devices");
        }
    }
}
