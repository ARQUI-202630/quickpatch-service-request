using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuickPatch.ServiceRequest.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Evidencias del servicio (RF-15, SCRUM-94; DD 5.10): tabla <c>service_evidence</c> con RLS por tenant. El rol de la
    /// aplicación solo inserta y lee: una evidencia no se cambia ni se borra. <c>file_url</c> guarda la clave del objeto en
    /// Garage (RN-E3).
    /// </summary>
    public partial class Evidencias : Migration
    {
        private const string TenantFilter = "tenant_id = NULLIF(current_setting('app.current_tenant', true), '')::uuid";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "service_evidence",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_evidence", x => x.id);
                    table.ForeignKey(
                        name: "FK_service_evidence_service_requests_service_request_id",
                        column: x => x.service_request_id,
                        principalTable: "service_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_service_evidence_service_request",
                table: "service_evidence",
                column: "service_request_id");

            // roles.sql solo corre en el primer despliegue: el dueño de la tabla concede los permisos si el rol ya existe.
            migrationBuilder.Sql($"""
                ALTER TABLE service_evidence ENABLE ROW LEVEL SECURITY;
                ALTER TABLE service_evidence FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON service_evidence USING ({TenantFilter}) WITH CHECK ({TenantFilter});

                DO $$
                BEGIN
                    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'service_request_app') THEN
                        GRANT SELECT, INSERT ON service_evidence TO service_request_app;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "service_evidence");
        }
    }
}
