using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace QuickPatch.ServiceRequest.Infrastructure.Persistence;

/// <summary>
/// Fábrica para <c>dotnet ef</c> (generar migraciones). No se conecta a ninguna base:
/// la conexión real de las migraciones la da el paso de despliegue con el rol de migraciones.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ServiceRequestDbContext>
{
    public ServiceRequestDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ServiceRequestDbContext>()
            .UseNpgsql("Host=localhost;Database=db_service_request", npgsql => npgsql.UseNetTopologySuite())
            .Options;
        return new ServiceRequestDbContext(options);
    }
}