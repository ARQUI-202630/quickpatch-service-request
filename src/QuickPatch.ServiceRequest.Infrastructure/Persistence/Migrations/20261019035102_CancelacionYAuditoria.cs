using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuickPatch.ServiceRequest.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Cancelación (RF-36, SCRUM-475): quién canceló (<c>cancelled_by</c>, <c>cancelled_by_type</c>) y la tabla <c>audit_logs</c>
    /// propia de ServiceRequest para <c>cancelar_solicitud_admin</c> (DD 5.14 y RN-SR8, v5.3).
    /// </summary>
    public partial class CancelacionYAuditoria : Migration
    {
        private const string TenantFilter = "tenant_id = NULLIF(current_setting('app.current_tenant', true), '')::uuid";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "cancelled_by",
                table: "service_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cancelled_by_type",
                table: "service_requests",
                type: "character varying(15)",
                maxLength: 15,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_logs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_tenant_created",
                table: "audit_logs",
                columns: new[] { "tenant_id", "created_at" });

            // RN-SR8: solo cliente, admin_tenant o sistema. audit_logs (DD 5.14, v5.3): RLS por tenant como las demás tablas
            // de negocio, y el rol de la aplicación solo inserta y lee (RN-A1). roles.sql solo corre en el primer despliegue:
            // el dueño de la tabla concede el permiso si el rol ya existe.
            migrationBuilder.Sql($"""
                ALTER TABLE service_requests ADD CONSTRAINT ck_service_requests_cancelled_by_type
                    CHECK (cancelled_by_type IS NULL OR cancelled_by_type IN ('cliente', 'admin_tenant', 'sistema'));

                ALTER TABLE audit_logs ENABLE ROW LEVEL SECURITY;
                ALTER TABLE audit_logs FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON audit_logs USING ({TenantFilter}) WITH CHECK ({TenantFilter});

                DO $$
                BEGIN
                    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'service_request_app') THEN
                        GRANT INSERT, SELECT ON audit_logs TO service_request_app;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE service_requests DROP CONSTRAINT IF EXISTS ck_service_requests_cancelled_by_type;");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropColumn(
                name: "cancelled_by",
                table: "service_requests");

            migrationBuilder.DropColumn(
                name: "cancelled_by_type",
                table: "service_requests");
        }
    }
}
