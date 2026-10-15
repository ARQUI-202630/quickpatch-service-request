using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuickPatch.ServiceRequest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IndiceHistorialTecnico : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_service_requests_tenant_technician_status",
                table: "service_requests",
                columns: new[] { "tenant_id", "technician_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_service_requests_tenant_technician_status",
                table: "service_requests");
        }
    }
}
