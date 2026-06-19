using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleet.Orchestrator.Migrations
{
    /// <inheritdoc />
    public partial class AddMountDockerSock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MountDockerSock",
                table: "agents",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            // Backfill existing rows to true so no currently-running agent loses Docker access.
            migrationBuilder.Sql("UPDATE agents SET MountDockerSock = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MountDockerSock",
                table: "agents");
        }
    }
}
