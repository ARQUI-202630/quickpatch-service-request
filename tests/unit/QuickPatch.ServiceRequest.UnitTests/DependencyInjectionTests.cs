using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using QuickPatch.ServiceRequest.Application;
using QuickPatch.ServiceRequest.Infrastructure;

namespace QuickPatch.ServiceRequest.UnitTests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddApplication_RegistraLaCapaDeAplicacion()
    {
        var services = new ServiceCollection();

        Assert.Same(services, services.AddApplication());
    }

    [Fact]
    public void AddInfrastructure_RegistraLaCapaDeInfraestructura()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        Assert.Same(services, services.AddInfrastructure(configuration));
    }

    [Fact]
    public void AddApplication_SinServicios_LanzaExcepcion()
    {
        Assert.Throws<ArgumentNullException>(() => QuickPatch.ServiceRequest.Application.DependencyInjection.AddApplication(null!));
    }

    [Fact]
    public void AddInfrastructure_SinConfiguracion_LanzaExcepcion()
    {
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddInfrastructure(null!));
    }
}