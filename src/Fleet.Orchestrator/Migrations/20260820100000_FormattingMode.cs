using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fleet.Orchestrator.Migrations
{
    /// <inheritdoc />
    public partial class FormattingMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add FormattingMode as tinyint unsigned, default 0 (PlainText)
            migrationBuilder.AddColumn<byte>(
                name: "FormattingMode",
                table: "agents",
                type: "tinyint unsigned",
                nullable: false,
                defaultValue: (byte)0);

            // Migrate existing data: UseFormatter=true (1) → FormattingMode=1 (LegacyHtml)
            //                        UseFormatter=false (0) → FormattingMode=0 (PlainText)
            migrationBuilder.Sql(
                "UPDATE agents SET FormattingMode = CASE WHEN UseFormatter = 1 THEN 1 ELSE 0 END");

            migrationBuilder.DropColumn(
                name: "UseFormatter",
                table: "agents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UseFormatter",
                table: "agents",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            // Migrate back: FormattingMode > 0 → UseFormatter=true
            migrationBuilder.Sql(
                "UPDATE agents SET UseFormatter = CASE WHEN FormattingMode > 0 THEN 1 ELSE 0 END");

            migrationBuilder.DropColumn(
                name: "FormattingMode",
                table: "agents");
        }
    }
}
