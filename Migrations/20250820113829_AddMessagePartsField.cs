using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessageHub.Migrations
{
    /// <inheritdoc />
    public partial class AddMessagePartsField : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MessageParts",
                table: "Messages",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MessageParts",
                table: "Messages");
        }
    }
}
