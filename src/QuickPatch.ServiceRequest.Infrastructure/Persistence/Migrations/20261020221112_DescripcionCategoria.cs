using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuickPatch.ServiceRequest.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Descripción en la réplica de categorías (ADR-025): ServiceRequest sirve la lista de categorías de las apps (contrato
    /// 1.8.0) y <c>catalog.category-changed</c> la trae desde quickpatch-kafka v0.8.0. Catalog vuelve a publicar todas las
    /// categorías al desplegar su migración <c>PublicarDescripcion</c>.
    /// </summary>
    public partial class DescripcionCategoria : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "description",
                table: "service_request_categories",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "description",
                table: "service_request_categories");
        }
    }
}
