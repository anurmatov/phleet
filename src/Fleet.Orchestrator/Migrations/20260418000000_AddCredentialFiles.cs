using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleet.Orchestrator.Migrations;

/// <inheritdoc />
public partial class AddCredentialFiles : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "credential_files",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("MySQL:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                Name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                Type = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false, defaultValue: "generic"),
                FileName = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false),
                FilePath = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false),
                SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_credential_files", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "agent_credential_mounts",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("MySQL:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                AgentId = table.Column<int>(type: "int", nullable: false),
                CredentialFileId = table.Column<int>(type: "int", nullable: false),
                MountPath = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false),
                Mode = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, defaultValue: "ro")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_agent_credential_mounts", x => x.Id);
                table.ForeignKey(
                    name: "FK_agent_credential_mounts_agents_AgentId",
                    column: x => x.AgentId,
                    principalTable: "agents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_agent_credential_mounts_credential_files_CredentialFileId",
                    column: x => x.CredentialFileId,
                    principalTable: "credential_files",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_credential_files_Name",
            table: "credential_files",
            column: "Name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_agent_credential_mounts_AgentId_CredentialFileId_MountPath",
            table: "agent_credential_mounts",
            columns: new[] { "AgentId", "CredentialFileId", "MountPath" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_agent_credential_mounts_CredentialFileId",
            table: "agent_credential_mounts",
            column: "CredentialFileId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "agent_credential_mounts");
        migrationBuilder.DropTable(name: "credential_files");
    }
}
