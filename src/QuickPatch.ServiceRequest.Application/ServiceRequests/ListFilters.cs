using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.Common;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.ServiceRequests;

/// <summary>Validación de <c>status</c>, <c>page</c> y <c>size</c> de las listas (DD 8.1): página desde 1, hasta 100 elementos.</summary>
public static class ListFilters
{
    public const int DefaultSize = 20;
    public const int MaxSize = 100;

    /// <summary>Devuelve la página y el tamaño efectivos; lanza <see cref="DomainValidationException"/> con cada campo inválido.</summary>
    public static (int Page, int Size) Validate(string? status, int? page, int? size)
    {
        var effectivePage = page ?? 1;
        var effectiveSize = size ?? DefaultSize;
        var errors = new Dictionary<string, string[]>();
        if (status is not null && !ServiceRequestStatus.All.Contains(status))
        {
            errors["status"] = ["El estado no existe."];
        }

        if (effectivePage < 1)
        {
            errors["page"] = ["La página empieza en 1."];
        }

        if (effectiveSize is < 1 or > MaxSize)
        {
            errors["size"] = [$"El tamaño de página va de 1 a {MaxSize}."];
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        return (effectivePage, effectiveSize);
    }
}