using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleet.Orchestrator.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DisplayName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Role = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Model = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Provider = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, defaultValue: "claude")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MemoryLimitMb = table.Column<int>(type: "int", nullable: false),
                    ContainerName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    PermissionMode = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false, defaultValue: "acceptEdits")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MaxTurns = table.Column<int>(type: "int", nullable: false, defaultValue: 50),
                    WorkDir = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false, defaultValue: "/workspace")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ProactiveIntervalMinutes = table.Column<int>(type: "int", nullable: false),
                    GroupListenMode = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false, defaultValue: "mention")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GroupDebounceSeconds = table.Column<int>(type: "int", nullable: false),
                    ShortName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false, defaultValue: "")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ShowStats = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    PrefixMessages = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    TelegramSendOnly = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    TtsServiceUrl = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Image = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Effort = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    JsonSchema = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AgentsJson = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    HostPort = table.Column<int>(type: "int", nullable: true),
                    AutoMemoryEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: true),
                    CodexSandboxMode = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agents", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "instructions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CurrentVersion = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_instructions", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "project_contexts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CurrentVersion = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_contexts", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "repositories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FullName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repositories", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "workflow_definitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Namespace = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TaskQueue = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "text", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Definition = table.Column<string>(type: "json", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Version = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_definitions", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "agent_env_refs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    EnvKeyName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_env_refs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_env_refs_agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "agent_mcp_endpoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    McpName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Url = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TransportType = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_mcp_endpoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_mcp_endpoints_agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "agent_networks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    NetworkName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_networks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_networks_agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "agent_projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    ProjectName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_projects_agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "agent_telegram_groups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    GroupId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_telegram_groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_telegram_groups_agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "agent_telegram_users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_telegram_users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_telegram_users_agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "agent_tools",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    ToolName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_tools", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_tools_agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "agent_instructions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    InstructionId = table.Column<int>(type: "int", nullable: false),
                    LoadOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_instructions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_instructions_agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_agent_instructions_instructions_InstructionId",
                        column: x => x.InstructionId,
                        principalTable: "instructions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "instruction_versions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    InstructionId = table.Column<int>(type: "int", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Reason = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_instruction_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_instruction_versions_instructions_InstructionId",
                        column: x => x.InstructionId,
                        principalTable: "instructions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "project_context_versions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ProjectContextId = table.Column<int>(type: "int", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Reason = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_context_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_project_context_versions_project_contexts_ProjectContextId",
                        column: x => x.ProjectContextId,
                        principalTable: "project_contexts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "workflow_definition_versions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    WorkflowDefinitionId = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Definition = table.Column<string>(type: "json", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Reason = table.Column<string>(type: "text", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_definition_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_workflow_definition_versions_workflow_definitions_WorkflowDe~",
                        column: x => x.WorkflowDefinitionId,
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_agent_env_refs_AgentId_EnvKeyName",
                table: "agent_env_refs",
                columns: new[] { "AgentId", "EnvKeyName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_instructions_AgentId_InstructionId",
                table: "agent_instructions",
                columns: new[] { "AgentId", "InstructionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_instructions_AgentId_LoadOrder",
                table: "agent_instructions",
                columns: new[] { "AgentId", "LoadOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_instructions_InstructionId",
                table: "agent_instructions",
                column: "InstructionId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_mcp_endpoints_AgentId_McpName",
                table: "agent_mcp_endpoints",
                columns: new[] { "AgentId", "McpName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_networks_AgentId_NetworkName",
                table: "agent_networks",
                columns: new[] { "AgentId", "NetworkName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_projects_AgentId_ProjectName",
                table: "agent_projects",
                columns: new[] { "AgentId", "ProjectName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_telegram_groups_AgentId_GroupId",
                table: "agent_telegram_groups",
                columns: new[] { "AgentId", "GroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_telegram_users_AgentId_UserId",
                table: "agent_telegram_users",
                columns: new[] { "AgentId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_tools_AgentId_ToolName",
                table: "agent_tools",
                columns: new[] { "AgentId", "ToolName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agents_IsEnabled",
                table: "agents",
                column: "IsEnabled");

            migrationBuilder.CreateIndex(
                name: "IX_agents_Name",
                table: "agents",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_instruction_versions_CreatedAt",
                table: "instruction_versions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_instruction_versions_InstructionId_VersionNumber",
                table: "instruction_versions",
                columns: new[] { "InstructionId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_instructions_Name",
                table: "instructions",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_context_versions_CreatedAt",
                table: "project_context_versions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_project_context_versions_ProjectContextId_VersionNumber",
                table: "project_context_versions",
                columns: new[] { "ProjectContextId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_contexts_Name",
                table: "project_contexts",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_repositories_FullName",
                table: "repositories",
                column: "FullName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_repositories_Name",
                table: "repositories",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_definition_versions_WorkflowDefinitionId_Version",
                table: "workflow_definition_versions",
                columns: new[] { "WorkflowDefinitionId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_definitions_IsActive",
                table: "workflow_definitions",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_definitions_Name",
                table: "workflow_definitions",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_env_refs");

            migrationBuilder.DropTable(
                name: "agent_instructions");

            migrationBuilder.DropTable(
                name: "agent_mcp_endpoints");

            migrationBuilder.DropTable(
                name: "agent_networks");

            migrationBuilder.DropTable(
                name: "agent_projects");

            migrationBuilder.DropTable(
                name: "agent_telegram_groups");

            migrationBuilder.DropTable(
                name: "agent_telegram_users");

            migrationBuilder.DropTable(
                name: "agent_tools");

            migrationBuilder.DropTable(
                name: "instruction_versions");

            migrationBuilder.DropTable(
                name: "project_context_versions");

            migrationBuilder.DropTable(
                name: "repositories");

            migrationBuilder.DropTable(
                name: "workflow_definition_versions");

            migrationBuilder.DropTable(
                name: "agents");

            migrationBuilder.DropTable(
                name: "instructions");

            migrationBuilder.DropTable(
                name: "project_contexts");

            migrationBuilder.DropTable(
                name: "workflow_definitions");
        }
    }
}
