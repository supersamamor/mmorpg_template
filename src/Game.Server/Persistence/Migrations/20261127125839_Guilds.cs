using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Game.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Guilds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "guilds",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    emblem = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    notice = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    founded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guilds", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "guild_members",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    character_id = table.Column<Guid>(type: "uuid", nullable: false),
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    rank = table.Column<int>(type: "integer", nullable: false),
                    joined_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guild_members", x => x.id);
                    table.CheckConstraint("ck_guild_members_rank", "rank >= 0");
                    table.ForeignKey(
                        name: "FK_guild_members_characters_character_id",
                        column: x => x.character_id,
                        principalTable: "characters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_guild_members_guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "guild_ranks",
                columns: table => new
                {
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    permissions = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guild_ranks", x => new { x.guild_id, x.position });
                    table.CheckConstraint("ck_guild_ranks_position", "position >= 0");
                    table.ForeignKey(
                        name: "FK_guild_ranks_guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_guild_members_guild",
                table: "guild_members",
                column: "guild_id");

            migrationBuilder.CreateIndex(
                name: "ux_guild_members_character",
                table: "guild_members",
                column: "character_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_guilds_name_key",
                table: "guilds",
                column: "name_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "guild_members");

            migrationBuilder.DropTable(
                name: "guild_ranks");

            migrationBuilder.DropTable(
                name: "guilds");
        }
    }
}
