using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleet.Orchestrator.Migrations;

/// <inheritdoc />
public partial class AddAccessRequestFields : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "CanReceiveChatRequests",
            table: "agents",
            type: "tinyint(1)",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "AccessRequestTargetAgent",
            table: "agents",
            type: "varchar(100)",
            maxLength: 100,
            nullable: true)
            .Annotation("MySql:CharSet", "utf8mb4");

        migrationBuilder.AddColumn<string>(
            name: "RequestReceivedMessage",
            table: "agents",
            type: "varchar(500)",
            maxLength: 500,
            nullable: true)
            .Annotation("MySql:CharSet", "utf8mb4");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "CanReceiveChatRequests", table: "agents");
        migrationBuilder.DropColumn(name: "AccessRequestTargetAgent", table: "agents");
        migrationBuilder.DropColumn(name: "RequestReceivedMessage", table: "agents");
    }
}
