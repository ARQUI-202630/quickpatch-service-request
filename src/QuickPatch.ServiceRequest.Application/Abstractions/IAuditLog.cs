using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Tenants;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Application.Abstractions;

/// <summary>
/// Auditoría de ServiceRequest (tabla <c>audit_logs</c> de <c>db_service_request</c>, DD 5.14, v5.3): la fila se guarda en
/// la misma transacción que la operación. Solo se insertan filas (RN-A1).
/// </summary>
public interface IAuditLog
{
    void Add(AuditEntry entry);
}

/// <summary>Fila de <c>audit_logs</c>. <see cref="Metadata"/> es JSON y nunca lleva datos personales del usuario.</summary>
public sealed record AuditEntry(Guid Id, Guid TenantId, Guid ActorUserId, string Action, Guid TargetId, string Metadata, DateTimeOffset CreatedAt);