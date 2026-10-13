using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleet.Orchestrator.Migrations
{
    /// <inheritdoc />
    public partial class AddAutonomyCharters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "epic_grants",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "delegated")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "PreflightJson",
                table: "epic_grants",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "PreflightSha256",
                table: "epic_grants",
                type: "char(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "epic_grant_bindings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    GrantId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Namespace = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Repo = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Issue = table.Column<long>(type: "bigint", nullable: false),
                    WorkflowId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RunId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ReviewRef = table.Column<string>(type: "char(64)", fixedLength: true, maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_epic_grant_bindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_epic_grant_bindings_epic_grants_GrantId",
                        column: x => x.GrantId,
                        principalTable: "epic_grants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "epic_grant_runs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    GrantId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Namespace = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    WorkflowId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RunId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    WorkflowType = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Repo = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Issue = table.Column<long>(type: "bigint", nullable: true),
                    MaxFixRound = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    FirstReservedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_epic_grant_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_epic_grant_runs_epic_grants_GrantId",
                        column: x => x.GrantId,
                        principalTable: "epic_grants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_epic_grant_bindings_GrantId_Repo_Issue",
                table: "epic_grant_bindings",
                columns: new[] { "GrantId", "Repo", "Issue" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_epic_grant_runs_GrantId_Namespace_WorkflowId_RunId",
                table: "epic_grant_runs",
                columns: new[] { "GrantId", "Namespace", "WorkflowId", "RunId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "epic_grant_bindings");

            migrationBuilder.DropTable(
                name: "epic_grant_runs");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "epic_grants");

            migrationBuilder.DropColumn(
                name: "PreflightJson",
                table: "epic_grants");

            migrationBuilder.DropColumn(
                name: "PreflightSha256",
                table: "epic_grants");
        }
    }
}
