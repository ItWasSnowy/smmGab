using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmmGab.Migrations
{
    /// <inheritdoc />
    public partial class AddBodyToPublication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Body",
                table: "Publications",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Body",
                table: "Publications");
        }
    }
}
