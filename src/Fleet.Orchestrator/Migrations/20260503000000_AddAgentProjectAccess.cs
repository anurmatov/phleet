using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleet.Orchestrator.Migrations;

/// <inheritdoc />
public partial class AddAgentProjectAccess : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "agent_project_access",
            columns: table => new
            {
                AgentName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                Project = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_agent_project_access", x => new { x.AgentName, x.Project });
            });

        migrationBuilder.CreateIndex(
            name: "IX_agent_project_access_AgentName",
            table: "agent_project_access",
            column: "AgentName");

        // Idempotent backfill: copy project access rows from the existing agent_projects junction table.
        // Uses INSERT IGNORE so re-running the migration is safe.
        // Agent names are normalized to lowercase at insert time.
        migrationBuilder.Sql(@"
            INSERT IGNORE INTO agent_project_access (AgentName, Project)
            SELECT LOWER(TRIM(a.Name)), LOWER(TRIM(ap.ProjectName))
            FROM agents a
            INNER JOIN agent_projects ap ON ap.AgentId = a.Id
            WHERE ap.ProjectName IS NOT NULL AND ap.ProjectName != '';
        ");

        // Seed wildcard '*' for all co-cto agents so they retain cross-project read access
        // after the ACL flag is enabled. Idempotent via INSERT IGNORE.
        migrationBuilder.Sql(@"
            INSERT IGNORE INTO agent_project_access (AgentName, Project)
            SELECT LOWER(TRIM(a.Name)), '*'
            FROM agents a
            WHERE LOWER(TRIM(a.Role)) = 'co-cto';
        ");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "agent_project_access");
    }
}
