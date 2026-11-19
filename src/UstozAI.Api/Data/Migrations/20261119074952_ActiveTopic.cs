using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UstozAI.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ActiveTopic : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActiveTopic",
                table: "conversation_sessions",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActiveTopic",
                table: "conversation_sessions");
        }
    }
}
