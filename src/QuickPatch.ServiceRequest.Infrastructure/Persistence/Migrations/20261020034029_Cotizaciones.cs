using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuickPatch.ServiceRequest.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Cotizaciones (RF-34, RF-35, SCRUM-488; DD 5.11, v5.5): tabla <c>quotes</c> con RLS por tenant, un CHECK por estado y
    /// montos, una sola pendiente y una sola aceptada por solicitud (RN-Q2, RN-Q4), y los permisos del rol de la aplicación y
    /// del rol de las tareas programadas (P-06), que solo lee lo necesario para encontrar las vencidas (RN-Q6).
    /// </summary>
    public partial class Cotizaciones : Migration
    {
        private const string TenantFilter = "tenant_id = NULLIF(current_setting('app.current_tenant', true), '')::uuid";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "quotes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_id = table.Column<Guid>(type: "uuid", nullable: false),
                    labor_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    materials_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false, computedColumnSql: "labor_amount + materials_amount", stored: true),
                    detail = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    responded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rejection_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quotes", x => x.id);
                    table.ForeignKey(
                        name: "FK_quotes_service_requests_service_request_id",
                        column: x => x.service_request_id,
                        principalTable: "service_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_quotes_pending_expires",
                table: "quotes",
                column: "expires_at",
                filter: "status = 'pendiente'");

            migrationBuilder.CreateIndex(
                name: "ix_quotes_service_request",
                table: "quotes",
                column: "service_request_id");

            migrationBuilder.CreateIndex(
                name: "ux_quotes_accepted",
                table: "quotes",
                column: "service_request_id",
                unique: true,
                filter: "status = 'aceptada'");

            migrationBuilder.CreateIndex(
                name: "ux_quotes_pending",
                table: "quotes",
                column: "service_request_id",
                unique: true,
                filter: "status = 'pendiente'");

            // roles.sql solo corre en el primer despliegue: el dueño de la tabla concede los permisos si los roles ya existen.
            migrationBuilder.Sql($"""
                ALTER TABLE quotes ADD CONSTRAINT ck_quotes_status
                    CHECK (status IN ('pendiente', 'aceptada', 'rechazada', 'anulada', 'expirada'));
                ALTER TABLE quotes ADD CONSTRAINT ck_quotes_amounts CHECK (labor_amount > 0 AND materials_amount >= 0);
                ALTER TABLE quotes ADD CONSTRAINT ck_quotes_expires CHECK (expires_at > created_at);
                ALTER TABLE quotes ADD CONSTRAINT ck_quotes_rejection_reason
                    CHECK (rejection_reason IS NULL OR (status = 'rechazada' AND char_length(rejection_reason) BETWEEN 5 AND 500));

                ALTER TABLE quotes ENABLE ROW LEVEL SECURITY;
                ALTER TABLE quotes FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON quotes USING ({TenantFilter}) WITH CHECK ({TenantFilter});

                DO $$
                BEGIN
                    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'service_request_app') THEN
                        GRANT SELECT, INSERT, UPDATE ON quotes TO service_request_app;
                    END IF;
                    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'service_request_scheduler') THEN
                        GRANT SELECT (id, tenant_id, service_request_id, status, expires_at) ON quotes TO service_request_scheduler;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "quotes");
        }
    }
}
