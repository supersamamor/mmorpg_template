using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Game.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GuildStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "storage_currency",
                table: "guilds",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "guild_storage_holds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    character_id = table.Column<Guid>(type: "uuid", nullable: false),
                    move = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    entry_id = table.Column<long>(type: "bigint", nullable: true),
                    item_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    cards = table.Column<string[]>(type: "text[]", nullable: false),
                    refine = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guild_storage_holds", x => x.id);
                    table.CheckConstraint("ck_guild_storage_holds_amounts", "quantity >= 0 AND currency >= 0");
                    table.ForeignKey(
                        name: "FK_guild_storage_holds_guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "guild_storage_items",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false),
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    item_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    cards = table.Column<string[]>(type: "text[]", nullable: false),
                    refine = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guild_storage_items", x => x.id);
                    table.CheckConstraint("ck_guild_storage_items_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_guild_storage_items_guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "guild_storage_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    character_id = table.Column<Guid>(type: "uuid", nullable: true),
                    move = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    item_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    entry_id = table.Column<long>(type: "bigint", nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<long>(type: "bigint", nullable: false),
                    at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guild_storage_log", x => x.id);
                    table.ForeignKey(
                        name: "FK_guild_storage_log_guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_guild_storage_holds_character",
                table: "guild_storage_holds",
                column: "character_id");

            migrationBuilder.CreateIndex(
                name: "ix_guild_storage_holds_guild",
                table: "guild_storage_holds",
                column: "guild_id");

            migrationBuilder.CreateIndex(
                name: "ix_guild_storage_items_guild",
                table: "guild_storage_items",
                column: "guild_id");

            migrationBuilder.CreateIndex(
                name: "ix_guild_storage_log_character",
                table: "guild_storage_log",
                column: "character_id");

            migrationBuilder.CreateIndex(
                name: "ix_guild_storage_log_guild_at",
                table: "guild_storage_log",
                columns: new[] { "guild_id", "at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "guild_storage_holds");

            migrationBuilder.DropTable(
                name: "guild_storage_items");

            migrationBuilder.DropTable(
                name: "guild_storage_log");

            migrationBuilder.DropColumn(
                name: "storage_currency",
                table: "guilds");
        }
    }
}
