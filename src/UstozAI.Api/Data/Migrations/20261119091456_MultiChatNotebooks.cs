using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UstozAI.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MultiChatNotebooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_conversation_sessions_UserId_SessionDate",
                table: "conversation_sessions");

            migrationBuilder.CreateTable(
                name: "notebooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Content = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notebooks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_notebooks_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_conversation_sessions_UserId_LastMessageAt",
                table: "conversation_sessions",
                columns: new[] { "UserId", "LastMessageAt" });

            migrationBuilder.CreateIndex(
                name: "IX_conversation_sessions_UserId_SessionDate",
                table: "conversation_sessions",
                columns: new[] { "UserId", "SessionDate" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_notebooks_UserId_UpdatedAt",
                table: "notebooks",
                columns: new[] { "UserId", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notebooks");

            migrationBuilder.DropIndex(
                name: "IX_conversation_sessions_UserId_LastMessageAt",
                table: "conversation_sessions");

            migrationBuilder.DropIndex(
                name: "IX_conversation_sessions_UserId_SessionDate",
                table: "conversation_sessions");

            migrationBuilder.CreateIndex(
                name: "IX_conversation_sessions_UserId_SessionDate",
                table: "conversation_sessions",
                columns: new[] { "UserId", "SessionDate" },
                unique: true,
                descending: new[] { false, true });
        }
    }
}
