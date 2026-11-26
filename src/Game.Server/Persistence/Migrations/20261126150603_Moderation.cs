using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Game.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Moderation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_sanctions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    audit_id = table.Column<long>(type: "bigint", nullable: false),
                    lifted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    lifted_audit_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_sanctions", x => x.id);
                    table.CheckConstraint("ck_account_sanctions_kind", "kind IN ('ban', 'mute') AND (kind = 'ban' OR until IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_account_sanctions_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "admin_items",
                columns: table => new
                {
                    item_instance_id = table.Column<long>(type: "bigint", nullable: false),
                    item_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    audit_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_items", x => x.item_instance_id);
                });

            migrationBuilder.CreateTable(
                name: "support_requests",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    opened_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    opened_audit_id = table.Column<long>(type: "bigint", nullable: false),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    closed_audit_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_support_requests", x => x.id);
                    table.CheckConstraint("ck_support_requests_terms", "kind IN ('deletion', 'export') AND status IN ('open', 'done', 'cancelled')");
                    table.ForeignKey(
                        name: "FK_support_requests_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_account_sanctions_account",
                table: "account_sanctions",
                columns: new[] { "account_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_support_requests_account",
                table: "support_requests",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_support_requests_status",
                table: "support_requests",
                columns: new[] { "status", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_sanctions");

            migrationBuilder.DropTable(
                name: "admin_items");

            migrationBuilder.DropTable(
                name: "support_requests");
        }
    }
}
