using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Persistence.Migrations
{
    /// <summary>
    /// T-705: a character's looks as ids, on <c>characters</c>. The columns are nullable and nothing is filled in: a NULL part means "the content's default" and is resolved when the
    /// character is shown (<c>AppearanceRules.Resolve</c>), so characters that exist already get the defaults of the content of the day, and this migration names no content id
    /// that a fork would have to edit. Nothing is ever written back by a load.
    /// </summary>
    public partial class Appearance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "face_id",
                table: "characters",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "gender",
                table: "characters",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "hair_color_id",
                table: "characters",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "hairstyle_id",
                table: "characters",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "skin_tone_id",
                table: "characters",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "face_id",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "gender",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "hair_color_id",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "hairstyle_id",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "skin_tone_id",
                table: "characters");
        }
    }
}
