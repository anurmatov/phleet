using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleet.Orchestrator.Migrations
{
    /// <summary>
    /// Adds provenance to <c>agent_project_access</c> so the project-assignment hook can revoke the
    /// rows it created without touching an operator's hand-made grants, and backfills the rows that
    /// hook would have written had it existed.
    /// </summary>
    /// <inheritdoc />
    public partial class AddAgentProjectAccessSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every pre-existing row becomes 'manual'. That is the factually correct value, not
            // merely the conservative one: provisioning has never written this table, so every row
            // that exists today was created by hand. Defaulting them to 'assignment' would let the
            // first unassignment revoke a grant this feature never created.
            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "agent_project_access",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "manual")
                .Annotation("MySql:CharSet", "utf8mb4");

            // Backfill the rows missing for assignments that already exist. Without this, the first
            // agent switched to reading project detail from memory gets 403 on everything, because
            // the grant hook only fires on a *future* assignment event.
            //
            // 'manual' is right for these too — they were not created by the hook, so the hook must
            // not be able to remove them. Stated consequence, which is the accepted trade: a
            // pre-existing assignment is never auto-revoked; an operator who wants one gone deletes
            // it through manage_agent_project_access.
            //
            // INSERT IGNORE keeps this idempotent and leaves an existing row's Source untouched, so
            // a wildcard or hand-added row is never rewritten.
            migrationBuilder.Sql(@"
                INSERT IGNORE INTO agent_project_access (AgentName, Project, Source)
                SELECT LOWER(TRIM(a.Name)), LOWER(TRIM(ap.ProjectName)), 'manual'
                FROM agents a
                INNER JOIN agent_projects ap ON ap.AgentId = a.Id
                WHERE ap.ProjectName IS NOT NULL AND TRIM(ap.ProjectName) != '';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only the column is reverted. Once Source is gone the backfilled rows are
            // indistinguishable from the operator rows that were already there, so deleting them
            // would revoke real grants.
            migrationBuilder.DropColumn(
                name: "Source",
                table: "agent_project_access");
        }
    }
}
