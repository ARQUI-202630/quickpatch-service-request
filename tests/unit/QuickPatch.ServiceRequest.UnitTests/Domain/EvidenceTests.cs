using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.UnitTests.Domain;

/// <summary>Evidencias e inicio y cierre del servicio en el dominio (DD 5.10, 8.3.10 y 7.4).</summary>
public class EvidenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 15, 0, 0, TimeSpan.Zero);

    public static TheoryData<byte[], string> Imagenes => new()
    {
        { [0xFF, 0xD8, 0xFF, 0xDB, 0, 0, 0, 0, 0, 0, 0, 0], EvidenceImage.Jpeg },
        { [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13], EvidenceImage.Png },
        { [.. "RIFF"u8, 0x24, 0, 0, 0, .. "WEBP"u8], EvidenceImage.WebP },
    };

    [Theory]
    [MemberData(nameof(Imagenes))]
    public void ReconoceElTipoPorElContenido(byte[] content, string expected) =>
        Assert.Equal(expected, EvidenceImage.Validate(content));

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 })] // GIF
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x41, 0x56, 0x45 })] // RIFF de audio (WAVE)
    [InlineData(new byte[] { 0xFF, 0xD8 })] // JPEG cortado
    public void RechazaVacioYOtrosTipos(byte[] content)
    {
        var ex = Assert.Throws<DomainValidationException>(() => EvidenceImage.Validate(content));
        Assert.True(ex.Errors.ContainsKey("file"));
    }

    [Fact]
    public void RechazaMasDeDiezMegas()
    {
        var content = new byte[ServiceEvidence.MaxBytes + 1];
        content[0] = 0xFF;
        content[1] = 0xD8;
        content[2] = 0xFF;

        var ex = Assert.Throws<DomainValidationException>(() => EvidenceImage.Validate(content));
        Assert.Contains("10 MB", ex.Errors["file"][0], StringComparison.Ordinal);
    }

    [Fact]
    public void LaClaveDelObjetoSoloUsaIdentificadores()
    {
        var tenant = Guid.NewGuid();
        var request = Guid.NewGuid();

        var evidence = ServiceEvidence.Create(tenant, request, Guid.NewGuid(), Now);

        Assert.Equal($"{tenant}/{request}/{evidence.Id}", evidence.FileUrl);
        Assert.Equal(Now, evidence.UploadedAt);
    }

    private static DomainServiceRequest Request(string status) => DomainServiceRequest.Restore(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Fuga de agua en la cocina",
        new GeoPoint(4.6486, -74.063), "Calle 63 # 9-45", status, Now.AddHours(-3), Now.AddHours(-2));

    [Fact]
    public void IniciaYCompletaEnOrden()
    {
        var request = Request(ServiceRequestStatus.CotizacionAceptada);

        Assert.False(request.TryComplete(Now));
        Assert.True(request.TryStart(Now));
        Assert.False(request.TryStart(Now.AddMinutes(1)));
        Assert.True(request.TryComplete(Now.AddHours(1)));

        Assert.Equal(ServiceRequestStatus.Completado, request.Status);
        Assert.Equal(Now, request.StartedAt);
        Assert.Equal(Now.AddHours(1), request.CompletedAt);
    }

    [Fact]
    public void EnProgreso_YaNoSeCancela()
    {
        var request = Request(ServiceRequestStatus.CotizacionAceptada);
        request.TryStart(Now);

        Assert.False(request.TryCancel(null, CancellationActor.Cliente, "Ya no lo necesito.", Now));
    }
}