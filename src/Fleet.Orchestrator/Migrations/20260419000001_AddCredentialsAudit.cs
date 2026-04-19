using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleet.Orchestrator.Migrations;

/// <inheritdoc />
public partial class AddCredentialsAudit : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "credentials_audit",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                KeyName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                ChangedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                Actor = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, defaultValue: "CEO")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_credentials_audit", x => x.Id);
            })
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.CreateIndex(
            name: "IX_credentials_audit_ChangedAt",
            table: "credentials_audit",
            column: "ChangedAt");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "credentials_audit");
    }
}
