using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleet.Orchestrator.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenShellExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "execution_shell",
                table: "agents",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "openshell")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "open_shell_migration_state",
                table: "agents",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "pending")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "open_shell_policy",
                table: "agents",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "normal")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "agent_open_shell_launches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    ActiveAgentId = table.Column<int>(type: "int", nullable: true),
                    ExpectedOldRuntimeId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GatewayId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SandboxId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Phase = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IdentityEpoch = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_open_shell_launches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_open_shell_launches_agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "agent_open_shell_leases",
                columns: table => new
                {
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    Owner = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ExpiresAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_open_shell_leases", x => x.AgentId);
                    table.ForeignKey(
                        name: "FK_agent_open_shell_leases_agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "open_shell_retired_ids",
                columns: table => new
                {
                    GatewayId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SandboxId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RetiredAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_open_shell_retired_ids", x => new { x.GatewayId, x.SandboxId });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_agent_open_shell_launches_ActiveAgentId",
                table: "agent_open_shell_launches",
                column: "ActiveAgentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_open_shell_launches_AgentId",
                table: "agent_open_shell_launches",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_open_shell_launches_GatewayId_SandboxId",
                table: "agent_open_shell_launches",
                columns: new[] { "GatewayId", "SandboxId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_open_shell_launches");

            migrationBuilder.DropTable(
                name: "agent_open_shell_leases");

            migrationBuilder.DropTable(
                name: "open_shell_retired_ids");

            migrationBuilder.DropColumn(
                name: "execution_shell",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "open_shell_migration_state",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "open_shell_policy",
                table: "agents");
        }
    }
}
