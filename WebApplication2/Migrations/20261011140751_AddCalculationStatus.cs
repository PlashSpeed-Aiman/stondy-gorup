using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApplication2.Migrations
{
    /// <inheritdoc />
    public partial class AddCalculationStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Error",
                table: "Geometries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Geometries",
                type: "TEXT",
                nullable: false,
                defaultValue: "Processing");

            // rows saved before the status column existed only contain results when they completed
            migrationBuilder.Sql("UPDATE Geometries SET Status = 'Completed' WHERE Data IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Error",
                table: "Geometries");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Geometries");
        }
    }
}
