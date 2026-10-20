using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.Quotes;

namespace QuickPatch.ServiceRequest.UnitTests.Domain;

/// <summary>Cotización (DD 5.11, RN-Q3 a RN-Q8; contrato 1.6.0).</summary>
public class QuoteTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 15, 20, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    private static Quote Issue(string? labor = "120000.00", string? materials = "65000.00", string? detail = "Cambio de sifón.") =>
        Quote.Issue(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), labor, materials, detail, Now, Ttl);

    [Fact]
    public void Emite_Pendiente_ConTotalYVencimiento()
    {
        var quote = Issue(detail: "  Cambio de sifón.  ");

        Assert.Equal(QuoteStatus.Pendiente, quote.Status);
        Assert.Equal(120000.00m, quote.LaborAmount);
        Assert.Equal(65000.00m, quote.MaterialsAmount);
        Assert.Equal(185000.00m, quote.TotalAmount);
        Assert.Equal("185000.00", Quote.FormatMoney(quote.TotalAmount));
        Assert.Equal("Cambio de sifón.", quote.Detail);
        Assert.Equal(Now + Ttl, quote.ExpiresAt);
        Assert.Null(quote.RespondedAt);
    }

    [Fact]
    public void SinMateriales_EsCeroYSinDetalle_EsNull()
    {
        var quote = Issue(materials: "0.00", detail: "   ");

        Assert.Equal(0m, quote.MaterialsAmount);
        Assert.Null(quote.Detail);
    }

    [Theory]
    [InlineData("120000", "laborAmount")]
    [InlineData("120000.5", "laborAmount")]
    [InlineData("-1.00", "laborAmount")]
    [InlineData("1,000.00", "laborAmount")]
    [InlineData("12345678901.00", "laborAmount")]
    [InlineData(null, "laborAmount")]
    [InlineData("0.00", "laborAmount")]
    public void ManoDeObraInvalida_400ConElCampo(string? labor, string field)
    {
        var ex = Assert.Throws<DomainValidationException>(() => Issue(labor: labor));

        Assert.Contains(field, ex.Errors.Keys);
    }

    [Fact]
    public void MaterialesSinFormato_YDetalleLargo_SeReportanJuntos()
    {
        var ex = Assert.Throws<DomainValidationException>(() => Issue(materials: "abc", detail: new string('x', Quote.DetailMaxLength + 1)));

        Assert.Contains("materialsAmount", ex.Errors.Keys);
        Assert.Contains("detail", ex.Errors.Keys);
    }

    [Fact]
    public void AceptaDentroDelPlazo_YNoDosVeces()
    {
        var quote = Issue();

        Assert.True(quote.TryAccept(Now.AddHours(1)));
        Assert.Equal(QuoteStatus.Aceptada, quote.Status);
        Assert.Equal(Now.AddHours(1), quote.RespondedAt);
        Assert.False(quote.TryAccept(Now.AddHours(2)));
        Assert.False(quote.TryReject(null, Now.AddHours(2)));
        Assert.False(quote.TryAnnul());
    }

    [Fact]
    public void NoSeRespondeDespuesDelVencimiento()
    {
        var quote = Issue();

        Assert.False(quote.TryAccept(quote.ExpiresAt));
        Assert.False(quote.TryReject("Muy costosa.", quote.ExpiresAt.AddMinutes(1)));
        Assert.Equal(QuoteStatus.Pendiente, quote.Status);
    }

    [Fact]
    public void RechazaConMotivo()
    {
        var quote = Issue();

        Assert.True(quote.TryReject("El valor es muy alto.", Now.AddMinutes(5)));
        Assert.Equal(QuoteStatus.Rechazada, quote.Status);
        Assert.Equal("El valor es muy alto.", quote.RejectionReason);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("  Muy costosa.  ", "Muy costosa.")]
    public void MotivoDelRechazo_EsOpcional(string? reason, string? expected) =>
        Assert.Equal(expected, Quote.ValidateRejectionReason(reason));

    [Fact]
    public void MotivoDelRechazoCorto_400()
    {
        var ex = Assert.Throws<DomainValidationException>(() => Quote.ValidateRejectionReason("No"));

        Assert.Contains("reason", ex.Errors.Keys);
    }

    [Fact]
    public void Vence_SoloPendienteYDespuesDelPlazo()
    {
        var quote = Issue();

        Assert.False(quote.TryExpire(quote.ExpiresAt.AddSeconds(-1)));
        Assert.True(quote.TryExpire(quote.ExpiresAt));
        Assert.Equal(QuoteStatus.Expirada, quote.Status);
        Assert.False(quote.TryExpire(quote.ExpiresAt.AddHours(1)));
        Assert.False(quote.TryAccept(quote.ExpiresAt.AddHours(1)));
    }

    [Fact]
    public void SeAnulaSoloPendiente()
    {
        var quote = Issue();

        Assert.True(quote.TryAnnul());
        Assert.Equal(QuoteStatus.Anulada, quote.Status);
        Assert.False(quote.TryAnnul());
    }

    [Fact]
    public void PlazoNoPositivo_EsUnErrorDeConfiguracion() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Quote.Issue(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "1.00", "0.00", null, Now, TimeSpan.Zero));
}