using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuickPatch.ServiceRequest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SolicitudDeEmpresa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "client_type",
                table: "service_requests",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "hogar");

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "service_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "site_name",
                table: "service_requests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_service_requests_company",
                table: "service_requests",
                columns: new[] { "tenant_id", "company_id" },
                filter: "company_id IS NOT NULL");

            // RN-SR12 (DD 5.5, v5.13): el tipo sale del rol y la empresa del token; las solicitudes anteriores quedan en hogar.
            migrationBuilder.Sql("""
                ALTER TABLE service_requests ADD CONSTRAINT ck_service_requests_client_type
                    CHECK (client_type IN ('hogar', 'empresa') AND (client_type = 'empresa') = (company_id IS NOT NULL));
                ALTER TABLE service_requests ADD CONSTRAINT ck_service_requests_site_name
                    CHECK (site_name IS NULL OR company_id IS NOT NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE service_requests DROP CONSTRAINT IF EXISTS ck_service_requests_site_name;
                ALTER TABLE service_requests DROP CONSTRAINT IF EXISTS ck_service_requests_client_type;
                """);

            migrationBuilder.DropIndex(
                name: "ix_service_requests_company",
                table: "service_requests");

            migrationBuilder.DropColumn(
                name: "client_type",
                table: "service_requests");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "service_requests");

            migrationBuilder.DropColumn(
                name: "site_name",
                table: "service_requests");
        }
    }
}
