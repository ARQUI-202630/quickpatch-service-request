using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.ServiceRequests;

/// <summary>Inicio del día calendario de Bogotá (UTC-5, sin horario de verano), que es el «hoy» de <c>createdToday</c>.</summary>
public static class BogotaDay
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(-5);

    public static DateTimeOffset StartUtc(DateTimeOffset now)
    {
        var local = now.ToOffset(Offset);
        return new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, Offset).ToUniversalTime();
    }
}