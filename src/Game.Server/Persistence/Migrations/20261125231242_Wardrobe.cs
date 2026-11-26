using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Wardrobe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "outfit_slots",
                table: "character_states",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<bool>(
                name: "outfits_hidden",
                table: "character_states",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "character_outfits",
                columns: table => new
                {
                    character_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outfit_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_character_outfits", x => new { x.character_id, x.outfit_id });
                    table.ForeignKey(
                        name: "FK_character_outfits_character_states_character_id",
                        column: x => x.character_id,
                        principalTable: "character_states",
                        principalColumn: "character_id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "character_outfits");

            migrationBuilder.DropColumn(
                name: "outfit_slots",
                table: "character_states");

            migrationBuilder.DropColumn(
                name: "outfits_hidden",
                table: "character_states");
        }
    }
}
