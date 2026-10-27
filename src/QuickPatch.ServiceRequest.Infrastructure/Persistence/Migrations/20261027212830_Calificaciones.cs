using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuickPatch.ServiceRequest.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Calificación del servicio (RF-12, SCRUM-85; DD 5.9, ADR-027): tabla <c>ratings</c> con RLS por tenant. RN-R1 es el
    /// CHECK del puntaje y RN-R2 el índice único por solicitud. El rol de la aplicación solo inserta y lee: una calificación
    /// no se edita ni se borra.
    /// </summary>
    public partial class Calificaciones : Migration
    {
        private const string TenantFilter = "tenant_id = NULLIF(current_setting('app.current_tenant', true), '')::uuid";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ratings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_id = table.Column<Guid>(type: "uuid", nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ratings", x => x.id);
                    table.CheckConstraint("ck_ratings_score", "score BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "FK_ratings_service_requests_service_request_id",
                        column: x => x.service_request_id,
                        principalTable: "service_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_ratings_service_request",
                table: "ratings",
                column: "service_request_id",
                unique: true);

            // roles.sql solo corre en el primer despliegue: el dueño de la tabla concede los permisos si el rol ya existe.
            migrationBuilder.Sql($"""
                ALTER TABLE ratings ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ratings FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON ratings USING ({TenantFilter}) WITH CHECK ({TenantFilter});

                DO $$
                BEGIN
                    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'service_request_app') THEN
                        GRANT SELECT, INSERT ON ratings TO service_request_app;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ratings");
        }
    }
}
