using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace QuickPatch.ServiceRequest.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Copia de solo lectura de las 20 localidades de Bogotá para la vista previa de la ubicación (P-09, DD 5.18): la
    /// misma capa IDECA que la migración V3 de Matching, sin tenant_id ni RLS y con solo SELECT para service_request_app.
    /// </summary>
    public partial class LocalidadesBogota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "localities",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    area = table.Column<MultiPolygon>(type: "geometry(MultiPolygon,4326)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_localities", x => x.code);
                });

            migrationBuilder.CreateIndex(
                name: "ux_localities_name",
                table: "localities",
                column: "name",
                unique: true);

            migrationBuilder.Sql(MigrationSql.Read("localities.sql"));

            // roles.sql solo corre en el primer despliegue: el dueño de la tabla concede el permiso si el rol ya existe.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'service_request_app') THEN
                        GRANT SELECT ON localities TO service_request_app;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "localities");
        }
    }
}
