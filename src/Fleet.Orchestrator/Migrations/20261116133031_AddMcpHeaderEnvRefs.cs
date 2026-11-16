using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleet.Orchestrator.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpHeaderEnvRefs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HeaderEnvRefsJson",
                table: "agent_mcp_endpoints",
                type: "text",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HeaderEnvRefsJson",
                table: "agent_mcp_endpoints");
        }
    }
}
