using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleet.Orchestrator.Migrations
{
    /// <inheritdoc />
    public partial class AddManagedStockRuntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppliedRuntimeBinding",
                table: "agents",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "AppliedRuntimeKind",
                table: "agents",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "docker")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "AppliedSandboxPolicyVersionId",
                table: "agents",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NativeAuthMode",
                table: "agents",
                type: "varchar(16)",
                maxLength: 16,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ProviderApiKeyEnvRef",
                table: "agents",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "RuntimeKind",
                table: "agents",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "docker")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "SandboxPolicyVersionId",
                table: "agents",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "sandbox_policies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sandbox_policies", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "sandbox_policy_versions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    PolicyId = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Yaml = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Sha256 = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ProviderProfileHashes = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sandbox_policy_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sandbox_policy_versions_sandbox_policies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "sandbox_policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_agents_AppliedSandboxPolicyVersionId",
                table: "agents",
                column: "AppliedSandboxPolicyVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_agents_SandboxPolicyVersionId",
                table: "agents",
                column: "SandboxPolicyVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_sandbox_policies_Name",
                table: "sandbox_policies",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sandbox_policy_versions_PolicyId_Version",
                table: "sandbox_policy_versions",
                columns: new[] { "PolicyId", "Version" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_agents_sandbox_policy_versions_AppliedSandboxPolicyVersionId",
                table: "agents",
                column: "AppliedSandboxPolicyVersionId",
                principalTable: "sandbox_policy_versions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_agents_sandbox_policy_versions_SandboxPolicyVersionId",
                table: "agents",
                column: "SandboxPolicyVersionId",
                principalTable: "sandbox_policy_versions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_agents_sandbox_policy_versions_AppliedSandboxPolicyVersionId",
                table: "agents");

            migrationBuilder.DropForeignKey(
                name: "FK_agents_sandbox_policy_versions_SandboxPolicyVersionId",
                table: "agents");

            migrationBuilder.DropTable(
                name: "sandbox_policy_versions");

            migrationBuilder.DropTable(
                name: "sandbox_policies");

            migrationBuilder.DropIndex(
                name: "IX_agents_AppliedSandboxPolicyVersionId",
                table: "agents");

            migrationBuilder.DropIndex(
                name: "IX_agents_SandboxPolicyVersionId",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "AppliedRuntimeBinding",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "AppliedRuntimeKind",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "AppliedSandboxPolicyVersionId",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "NativeAuthMode",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "ProviderApiKeyEnvRef",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "RuntimeKind",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "SandboxPolicyVersionId",
                table: "agents");
        }
    }
}
