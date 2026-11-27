using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Game.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Chat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_blocks",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    blocked_character_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_blocks", x => new { x.account_id, x.blocked_character_id });
                    table.ForeignKey(
                        name: "FK_chat_blocks_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_chat_blocks_characters_blocked_character_id",
                        column: x => x.blocked_character_id,
                        principalTable: "characters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "chat_reports",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    reported_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    reporter_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reporter_character_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_character_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    said_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    closed_audit_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_reports", x => x.id);
                    table.CheckConstraint("ck_chat_reports_status", "status IN ('open', 'closed') AND (status = 'open') = (closed_at IS NULL)");
                    table.ForeignKey(
                        name: "FK_chat_reports_accounts_reporter_account_id",
                        column: x => x.reporter_account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_chat_reports_accounts_sender_account_id",
                        column: x => x.sender_account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_chat_reports_characters_reporter_character_id",
                        column: x => x.reporter_character_id,
                        principalTable: "characters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_chat_reports_characters_sender_character_id",
                        column: x => x.sender_character_id,
                        principalTable: "characters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_chat_blocks_blocked",
                table: "chat_blocks",
                column: "blocked_character_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_reports_reporter",
                table: "chat_reports",
                column: "reporter_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_chat_reports_reporter_character_id",
                table: "chat_reports",
                column: "reporter_character_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_reports_sender",
                table: "chat_reports",
                column: "sender_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_chat_reports_sender_character_id",
                table: "chat_reports",
                column: "sender_character_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_reports_status",
                table: "chat_reports",
                columns: new[] { "status", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_blocks");

            migrationBuilder.DropTable(
                name: "chat_reports");
        }
    }
}
