using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuickPatch.ServiceRequest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplicaEstadoTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_status_replica",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_status_replica", x => x.tenant_id);
                });

            migrationBuilder.Sql(TenantIsolationSql);
        }

        // Réplica del estado de los tenants (SCRUM-510, RN-T1): mismo aislamiento que las demás tablas del tenant (DD 10.2).
        private const string TenantFilter = "tenant_id = NULLIF(current_setting('app.current_tenant', true), '')::uuid";

        private static readonly string TenantIsolationSql = $"""
            ALTER TABLE tenant_status_replica
                ADD CONSTRAINT ck_tenant_status_replica_status CHECK (status IN ('activo', 'inactivo'));
            ALTER TABLE tenant_status_replica ENABLE ROW LEVEL SECURITY;
            ALTER TABLE tenant_status_replica FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON tenant_status_replica USING ({TenantFilter}) WITH CHECK ({TenantFilter});

            -- El consumidor escribe en esta tabla desde este despliegue; sin el permiso fallaría hasta que se vuelva a
            -- aplicar db/roles.sql. El dueño de la tabla (el migrador) concede el permiso si el rol ya existe.
            DO $$
            BEGIN
                IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'service_request_app') THEN
                    GRANT SELECT, INSERT, UPDATE ON tenant_status_replica TO service_request_app;
                END IF;
            END
            $$;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_status_replica");
        }
    }
}
