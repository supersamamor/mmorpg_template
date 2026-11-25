using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Game.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Characters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "server_runs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_server_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "character_states",
                columns: table => new
                {
                    character_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim = table.Column<long>(type: "bigint", nullable: false),
                    held_by = table.Column<long>(type: "bigint", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    saved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    map_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    cell_x = table.Column<int>(type: "integer", nullable: false),
                    cell_y = table.Column<int>(type: "integer", nullable: false),
                    facing = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    hp = table.Column<int>(type: "integer", nullable: false),
                    sp = table.Column<int>(type: "integer", nullable: false),
                    save_map_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    save_cell_x = table.Column<int>(type: "integer", nullable: true),
                    save_cell_y = table.Column<int>(type: "integer", nullable: true),
                    base_level = table.Column<int>(type: "integer", nullable: false),
                    base_exp = table.Column<long>(type: "bigint", nullable: false),
                    job_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    job_level = table.Column<int>(type: "integer", nullable: false),
                    job_exp = table.Column<long>(type: "bigint", nullable: false),
                    stat_points = table.Column<int>(type: "integer", nullable: false),
                    skill_points = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<long>(type: "bigint", nullable: false),
                    turn_in_sequence = table.Column<long>(type: "bigint", nullable: false),
                    autopilot_used_ms = table.Column<long>(type: "bigint", nullable: false),
                    autopilot_day_started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    stats = table.Column<string>(type: "jsonb", nullable: false),
                    skills = table.Column<string>(type: "jsonb", nullable: false),
                    quests = table.Column<string>(type: "jsonb", nullable: false),
                    cooldowns = table.Column<string>(type: "jsonb", nullable: false),
                    set_aside = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_character_states", x => x.character_id);
                    table.CheckConstraint("ck_character_states_counts", "claim >= 1 AND version >= 0 AND turn_in_sequence >= 0");
                    table.CheckConstraint("ck_character_states_currency", "currency >= 0");
                    table.ForeignKey(
                        name: "FK_character_states_characters_character_id",
                        column: x => x.character_id,
                        principalTable: "characters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_character_states_server_runs_held_by",
                        column: x => x.held_by,
                        principalTable: "server_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "character_items",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    character_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    slot = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    cards = table.Column<string[]>(type: "text[]", nullable: false),
                    refine = table.Column<int>(type: "integer", nullable: false),
                    aside = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_character_items", x => x.id);
                    table.CheckConstraint("ck_character_items_quantity", "quantity >= 1 AND refine >= 0");
                    table.ForeignKey(
                        name: "FK_character_items_character_states_character_id",
                        column: x => x.character_id,
                        principalTable: "character_states",
                        principalColumn: "character_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_character_items_character_id",
                table: "character_items",
                column: "character_id");

            migrationBuilder.CreateIndex(
                name: "ux_character_items_slot",
                table: "character_items",
                columns: new[] { "character_id", "slot" },
                unique: true,
                filter: "slot IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_character_states_held_by",
                table: "character_states",
                column: "held_by",
                filter: "held_by IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "character_items");

            migrationBuilder.DropTable(
                name: "character_states");

            migrationBuilder.DropTable(
                name: "server_runs");
        }
    }
}
