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

            // Backfill only the roles that had docker.sock access before this migration.
            // Agents outside these roles keep the safe default (false).
            migrationBuilder.Sql("UPDATE agents SET MountDockerSock = 1 WHERE Role IN ('co-cto', 'devops', 'developer')");
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
