using Microsoft.EntityFrameworkCore;

using NetTopologySuite.Geometries;

using QuickPatch.ServiceRequest.Application.Abstractions;
using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.Ratings;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;
using QuickPatch.ServiceRequest.Domain.Tenants;
using QuickPatch.Transversal.Messaging;
using QuickPatch.Transversal.Persistence;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Infrastructure.Persistence;

public sealed class EfAuditLog(ServiceRequestDbContext db) : IAuditLog
{
    public void Add(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        db.AuditLogs.Add(new AuditLogRecord
        {
            Id = entry.Id,
            TenantId = entry.TenantId,
            ActorUserId = entry.ActorUserId,
            Action = entry.Action,
            TargetId = entry.TargetId,
            Metadata = entry.Metadata,
            CreatedAt = entry.CreatedAt,
        });
    }
}