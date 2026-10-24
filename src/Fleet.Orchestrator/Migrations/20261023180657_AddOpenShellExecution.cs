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
                    Workspace = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PolicyHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ImageHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DestinationHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ControlUrl = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CapabilitySecretRef = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Error = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    BotPollerLeaseOwner = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    BotPollerLeaseExpiresAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    TelegramOffset = table.Column<long>(type: "bigint", nullable: true),
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
                name: "agent_secret_bindings",
                columns: table => new
                {
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    Reference = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Classification = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, defaultValue: "unclassified")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SourceKind = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Provider = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Alias = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ProfileHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EndpointHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ExpiresAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    RiskApprovalRef = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_secret_bindings", x => new { x.AgentId, x.Reference });
                    table.ForeignKey(
                        name: "FK_agent_secret_bindings_agents_AgentId",
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

            migrationBuilder.CreateTable(
                name: "agent_identity_sessions",
                columns: table => new
                {
                    SessionHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Service = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Route = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AgentId = table.Column<int>(type: "int", nullable: false),
                    LaunchId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    IdentityEpoch = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ExpiresAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_identity_sessions", x => new { x.SessionHash, x.Service, x.Route });
                    table.ForeignKey(
                        name: "FK_agent_identity_sessions_agent_open_shell_launches_LaunchId",
                        column: x => x.LaunchId,
                        principalTable: "agent_open_shell_launches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_identity_sessions_agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "identity_request_nonces",
                columns: table => new
                {
                    Jti = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LaunchId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ExpiresAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_request_nonces", x => x.Jti);
                    table.ForeignKey(
                        name: "FK_identity_request_nonces_agent_open_shell_launches_LaunchId",
                        column: x => x.LaunchId,
                        principalTable: "agent_open_shell_launches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_agent_identity_sessions_AgentId",
                table: "agent_identity_sessions",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_identity_sessions_LaunchId_ExpiresAt",
                table: "agent_identity_sessions",
                columns: new[] { "LaunchId", "ExpiresAt" });

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

            migrationBuilder.CreateIndex(
                name: "IX_identity_request_nonces_ExpiresAt",
                table: "identity_request_nonces",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_identity_request_nonces_LaunchId",
                table: "identity_request_nonces",
                column: "LaunchId");

            migrationBuilder.AddColumn<string>(
                name: "DeleteIntentJson",
                table: "agent_open_shell_launches",
                type: "varchar(2048)",
                maxLength: 2048,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "StopIntentJson",
                table: "agent_open_shell_launches",
                type: "varchar(2048)",
                maxLength: 2048,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_identity_sessions");

            migrationBuilder.DropTable(
                name: "agent_open_shell_leases");

            migrationBuilder.DropTable(
                name: "agent_secret_bindings");

            migrationBuilder.DropTable(
                name: "identity_request_nonces");

            migrationBuilder.DropTable(
                name: "open_shell_retired_ids");

            migrationBuilder.DropTable(
                name: "agent_open_shell_launches");

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
