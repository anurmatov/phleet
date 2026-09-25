using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleet.Orchestrator.Migrations
{
    /// <summary>
    /// Removes project cards, per-assignment context modes and project routes (#346). Every assignment
    /// now loads the project's canonical context in full.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>project_contexts</c> rows and <c>project_context_versions</c> are never written: card text is
    /// never copied into a canonical context. <c>ContextRemovalPreflight</c> refuses to apply this
    /// migration while a project that has a card or a card assignment lacks canonical content.
    /// </para>
    /// <para>
    /// ⚠️ MySQL DDL commits implicitly, so this migration is NOT atomic. A failure part-way needs the
    /// pre-deploy dump. The steps are ordered so that stopping after step 1 still leaves the previous
    /// build serving every agent in full.
    /// </para>
    /// </remarks>
    public partial class RemoveProjectContextCards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Every assignment becomes full.
            migrationBuilder.Sql("UPDATE agent_projects SET ContextMode = 'full' WHERE ContextMode <> 'full';");

            // 2. The reserved fallback endpoint and grant from #347 point at /mcp/context, which is gone.
            migrationBuilder.Sql("DELETE FROM agent_mcp_endpoints WHERE McpName = 'fleet-context';");
            migrationBuilder.Sql("DELETE FROM agent_tools WHERE ToolName = 'mcp__fleet-context__get_project_context';");

            // 3–6. The card schema.
            migrationBuilder.DropTable(
                name: "project_context_routes");

            migrationBuilder.DropTable(
                name: "project_context_card_versions");

            migrationBuilder.DropColumn(
                name: "CurrentCardVersion",
                table: "project_contexts");

            migrationBuilder.DropColumn(
                name: "ContextMode",
                table: "agent_projects");
        }

        /// <inheritdoc />
        /// <remarks>
        /// Recreates the schema of <c>AddProjectContextCards.Up</c> exactly and restores NO data: no card
        /// text, routes, modes or reserved rows. The pre-deploy dump is the only way to bring them back.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CurrentCardVersion",
                table: "project_contexts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContextMode",
                table: "agent_projects",
                type: "varchar(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "full")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "project_context_card_versions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ProjectContextId = table.Column<int>(type: "int", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    BasedOnFullVersion = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Reason = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_context_card_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_project_context_card_versions_project_contexts_ProjectContex~",
                        column: x => x.ProjectContextId,
                        principalTable: "project_contexts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "project_context_routes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ProjectContextId = table.Column<int>(type: "int", nullable: false),
                    SignalKind = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SignalValue = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_context_routes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_project_context_routes_project_contexts_ProjectContextId",
                        column: x => x.ProjectContextId,
                        principalTable: "project_contexts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_project_context_card_versions_ProjectContextId_VersionNumber",
                table: "project_context_card_versions",
                columns: new[] { "ProjectContextId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_context_routes_ProjectContextId",
                table: "project_context_routes",
                column: "ProjectContextId");

            migrationBuilder.CreateIndex(
                name: "IX_project_context_routes_SignalKind_SignalValue_ProjectContext~",
                table: "project_context_routes",
                columns: new[] { "SignalKind", "SignalValue", "ProjectContextId" },
                unique: true);
        }
    }
}
