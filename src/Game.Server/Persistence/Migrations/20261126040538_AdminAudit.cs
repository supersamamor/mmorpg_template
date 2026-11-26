using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Game.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_staff",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    display_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    first_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_staff", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "admin_audit",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    request_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    staff_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    staff_role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    target_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    target_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    before = table.Column<string>(type: "jsonb", nullable: false),
                    after = table.Column<string>(type: "jsonb", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_audit", x => x.id);
                    table.CheckConstraint("ck_admin_audit_reason", "length(btrim(reason)) > 0");
                    table.ForeignKey(
                        name: "FK_admin_audit_admin_staff_staff_id",
                        column: x => x.staff_id,
                        principalTable: "admin_staff",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_admin_audit_at",
                table: "admin_audit",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_admin_audit_staff",
                table: "admin_audit",
                columns: new[] { "staff_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_admin_audit_target",
                table: "admin_audit",
                columns: new[] { "target_id", "id" });

            // Append-only (ARCHITECTURE §12.3): no UPDATE, DELETE or TRUNCATE of an audit row, whoever asks, the table's owner included. Only DDL (dropping the
            // triggers) gets past this, and the server issues DDL only in migrations; docs/ADMIN_API.md "Audit log" says how a deployment takes even that away.
            migrationBuilder.Sql(@"
CREATE FUNCTION admin_audit_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'admin_audit is append-only: % refused', TG_OP USING ERRCODE = 'insufficient_privilege';
END
$$;
CREATE TRIGGER admin_audit_no_change BEFORE UPDATE OR DELETE ON admin_audit FOR EACH ROW EXECUTE FUNCTION admin_audit_append_only();
CREATE TRIGGER admin_audit_no_truncate BEFORE TRUNCATE ON admin_audit FOR EACH STATEMENT EXECUTE FUNCTION admin_audit_append_only();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER admin_audit_no_truncate ON admin_audit; DROP TRIGGER admin_audit_no_change ON admin_audit; DROP FUNCTION admin_audit_append_only();");

            migrationBuilder.DropTable(
                name: "admin_audit");

            migrationBuilder.DropTable(
                name: "admin_staff");
        }
    }
}
