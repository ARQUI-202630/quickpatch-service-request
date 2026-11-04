using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Domain;

/// <summary>Tipo de cliente, empresa y sede de la solicitud (RN-SR12, RN-SR14, contrato 1.9.0).</summary>
public class CompanyRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly GeoPoint Punto = new(4.6486, -74.063);

    private static DomainServiceRequest Create(Guid? company = null, string? site = null, Guid? client = null) =>
        DomainServiceRequest.Create(
            Guid.NewGuid(), client ?? Guid.NewGuid(), Guid.NewGuid(), "Fuga de agua en la cocina", Punto, "Calle 63 # 9-45", Now, company, site);

    [Fact]
    public void SinEmpresa_EsDeHogar_YConEmpresa_EsDeEmpresaConSuSede()
    {
        var hogar = Create();
        var empresa = Guid.NewGuid();
        var deEmpresa = Create(empresa, "  Sede Norte ");

        Assert.Equal(ClientTypes.Hogar, hogar.ClientType);
        Assert.Null(hogar.CompanyId);
        Assert.Equal(ClientTypes.Empresa, deEmpresa.ClientType);
        Assert.Equal(empresa, deEmpresa.CompanyId);
        Assert.Equal("Sede Norte", deEmpresa.SiteName);
        Assert.Null(Create(empresa, "   ").SiteName);
    }

    [Fact]
    public void Sede_SinEmpresaOFueraDeRango_EsError()
    {
        Assert.Contains("siteName", Assert.Throws<DomainValidationException>(() => Create(site: "Sede Norte")).Errors.Keys);
        Assert.Contains("siteName", Assert.Throws<DomainValidationException>(() => Create(Guid.NewGuid(), "A")).Errors.Keys);
        Assert.Contains("siteName", Assert.Throws<DomainValidationException>(() => Create(Guid.NewGuid(), new string('s', 101))).Errors.Keys);
        Assert.Contains("companyId", Assert.Throws<DomainValidationException>(() => Create(Guid.Empty)).Errors.Keys);
        Assert.Contains("siteName", Assert.Throws<DomainValidationException>(() => DomainServiceRequest.ValidateFieldsWithoutLocation(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Fuga de agua en la cocina", "Calle 63 # 9-45", null, "Sede")).Errors.Keys);
    }

    [Fact]
    public void LaVenQuienLaCreoYLosDeSuEmpresa_RnSr14()
    {
        var creador = Guid.NewGuid();
        var empresa = Guid.NewGuid();
        var deEmpresa = Create(empresa, client: creador);
        var hogar = Create(client: creador);

        Assert.True(deEmpresa.IsVisibleToRequester(creador, null));
        Assert.True(deEmpresa.IsVisibleToRequester(Guid.NewGuid(), empresa));
        Assert.False(deEmpresa.IsVisibleToRequester(Guid.NewGuid(), Guid.NewGuid()));
        Assert.False(deEmpresa.IsVisibleToRequester(Guid.NewGuid(), null));
        Assert.False(hogar.IsVisibleToRequester(Guid.NewGuid(), empresa));
    }
}