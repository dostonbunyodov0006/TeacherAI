using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UstozAI.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DailyConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data-preserving: learning_sessions → one conversation_sessions row per (user, local day);
            // chat_messages are re-pointed, then sidebar counters/titles are back-filled.
            migrationBuilder.AddColumn<string>(
                name: "TimeZone",
                table: "user_preferences",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Asia/Tashkent");

            migrationBuilder.CreateTable(
                name: "conversation_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    MessageCount = table.Column<int>(type: "integer", nullable: false),
                    LastMessageAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_conversation_sessions_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.DropForeignKey(
                name: "FK_chat_messages_learning_sessions_SessionId",
                table: "chat_messages");

            migrationBuilder.Sql("""
                CREATE TEMP TABLE _session_map ON COMMIT DROP AS
                SELECT ls."Id" AS old_id, ls."UserId" AS uid, ls."Language" AS lang, ls."CreatedAt" AS created,
                       (ls."CreatedAt" AT TIME ZONE COALESCE(p."TimeZone", 'Asia/Tashkent'))::date AS d
                FROM learning_sessions ls LEFT JOIN user_preferences p ON p."UserId" = ls."UserId";

                INSERT INTO conversation_sessions ("Id","UserId","SessionDate","Title","Language","MessageCount","LastMessageAt","CreatedAt","UpdatedAt")
                SELECT gen_random_uuid(), uid, d, '', (array_agg(lang ORDER BY created))[1], 0, NULL, min(created), now()
                FROM _session_map GROUP BY uid, d;

                UPDATE chat_messages m SET "SessionId" = cs."Id"
                FROM _session_map sm JOIN conversation_sessions cs ON cs."UserId" = sm.uid AND cs."SessionDate" = sm.d
                WHERE m."SessionId" = sm.old_id;

                UPDATE conversation_sessions cs SET "MessageCount" = s.c, "LastMessageAt" = s.last
                FROM (SELECT "SessionId", count(*) AS c, max("CreatedAt") AS last FROM chat_messages GROUP BY 1) s
                WHERE s."SessionId" = cs."Id";

                UPDATE conversation_sessions cs SET "Title" = left(f."Content", 80)
                FROM (SELECT DISTINCT ON ("SessionId") "SessionId", "Content" FROM chat_messages
                      WHERE "Role" = 'User' ORDER BY "SessionId", "Id") f
                WHERE f."SessionId" = cs."Id";
                """);

            migrationBuilder.DropTable(name: "learning_sessions");

            migrationBuilder.CreateIndex(
                name: "IX_conversation_sessions_UserId_SessionDate",
                table: "conversation_sessions",
                columns: new[] { "UserId", "SessionDate" },
                unique: true,
                descending: new[] { false, true });

            migrationBuilder.AddForeignKey(
                name: "FK_chat_messages_conversation_sessions_SessionId",
                table: "chat_messages",
                column: "SessionId",
                principalTable: "conversation_sessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_chat_messages_conversation_sessions_SessionId",
                table: "chat_messages");

            migrationBuilder.DropTable(
                name: "conversation_sessions");

            migrationBuilder.DropColumn(
                name: "TimeZone",
                table: "user_preferences");

            migrationBuilder.CreateTable(
                name: "learning_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_learning_sessions_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_learning_sessions_UserId_UpdatedAt",
                table: "learning_sessions",
                columns: new[] { "UserId", "UpdatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_chat_messages_learning_sessions_SessionId",
                table: "chat_messages",
                column: "SessionId",
                principalTable: "learning_sessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
