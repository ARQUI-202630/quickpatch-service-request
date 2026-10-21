using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

using NetTopologySuite.Geometries;

using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.Evidence;
using QuickPatch.ServiceRequest.Domain.Quotes;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Infrastructure.Persistence;

/// <summary>Base de datos de ServiceRequest Service (DD, secciones 5.5, 5.10, 5.11, 5.15, 5.16, 5.17 y la copia de 5.18).</summary>
public sealed class ServiceRequestDbContext(DbContextOptions<ServiceRequestDbContext> options) : DbContext(options)
{
    public DbSet<DomainServiceRequest> ServiceRequests => Set<DomainServiceRequest>();

    public DbSet<CategoryReplica> Categories => Set<CategoryReplica>();

    public DbSet<Quote> Quotes => Set<Quote>();

    public DbSet<ServiceEvidence> Evidence => Set<ServiceEvidence>();

    public DbSet<OutboxEventRecord> OutboxEvents => Set<OutboxEventRecord>();

    public DbSet<ProcessedEventRecord> ProcessedEvents => Set<ProcessedEventRecord>();

    public DbSet<LocalityRecord> Localities => Set<LocalityRecord>();

    public DbSet<AuditLogRecord> AuditLogs => Set<AuditLogRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("postgis");

        var location = new ValueConverter<GeoPoint, Point>(
            g => new Point(g.Longitude, g.Latitude) { SRID = 4326 },
            p => new GeoPoint(p.Y, p.X));

        modelBuilder.Entity<DomainServiceRequest>(e =>
        {
            e.ToTable("service_requests");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ClientId).HasColumnName("client_id");
            e.Property(x => x.CategoryId).HasColumnName("category_id");
            e.Property(x => x.TechnicianId).HasColumnName("technician_id");
            e.Property(x => x.Description).HasColumnName("description").HasColumnType("text");
            e.Property(x => x.Location).HasColumnName("location").HasColumnType("geometry(Point,4326)").HasConversion(location);
            e.Property(x => x.AddressText).HasColumnName("address_text").HasMaxLength(255);
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(30);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.MatchingUpdatedAt).HasColumnName("matching_updated_at");
            e.Property(x => x.AssignedAt).HasColumnName("assigned_at");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.Property(x => x.CancelledAt).HasColumnName("cancelled_at");
            e.Property(x => x.CancellationReason).HasColumnName("cancellation_reason").HasColumnType("text");
            // RF-36, RN-SR8 (DD 5.5, v5.3): quién canceló; el CHECK lo agrega la migración CancelacionYAuditoria.
            e.Property(x => x.CancelledBy).HasColumnName("cancelled_by");
            e.Property(x => x.CancelledByType).HasColumnName("cancelled_by_type").HasMaxLength(15);
            // Solicitud de empresa (RF-08, DD 5.5 v5.13): el CHECK de client_type lo agrega la migración SolicitudDeEmpresa.
            e.Property(x => x.ClientType).HasColumnName("client_type").HasMaxLength(10).HasDefaultValue("hogar");
            e.Property(x => x.CompanyId).HasColumnName("company_id");
            e.Property(x => x.SiteName).HasColumnName("site_name").HasMaxLength(DomainServiceRequest.SiteNameMaxLength);
            e.HasIndex(x => new { x.TenantId, x.CompanyId }).HasDatabaseName("ix_service_requests_company").HasFilter("company_id IS NOT NULL");
            e.HasIndex(x => new { x.TenantId, x.ClientId }).HasDatabaseName("ix_service_requests_tenant_client");
            // Historial del técnico (RF-17, DD 5: índice sobre technician_id y status).
            e.HasIndex(x => new { x.TenantId, x.TechnicianId, x.Status }).HasDatabaseName("ix_service_requests_tenant_technician_status");
            e.HasIndex(x => x.Location).HasDatabaseName("ix_service_requests_location").HasMethod("gist");
        });

        // Cotizaciones (DD 5.11, RF-34, RF-35). Los CHECK, los índices únicos parciales (RN-Q2, RN-Q4) y RLS los agrega la
        // migración Cotizaciones; total_amount es una columna generada.
        modelBuilder.Entity<Quote>(e =>
        {
            e.ToTable("quotes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ServiceRequestId).HasColumnName("service_request_id");
            e.Property(x => x.TechnicianId).HasColumnName("technician_id");
            e.Property(x => x.LaborAmount).HasColumnName("labor_amount").HasPrecision(12, 2);
            e.Property(x => x.MaterialsAmount).HasColumnName("materials_amount").HasPrecision(12, 2);
            e.Property(x => x.TotalAmount).HasColumnName("total_amount").HasPrecision(12, 2)
                .HasComputedColumnSql("labor_amount + materials_amount", stored: true);
            e.Property(x => x.Detail).HasColumnName("detail").HasColumnType("text");
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.RespondedAt).HasColumnName("responded_at");
            e.Property(x => x.RejectionReason).HasColumnName("rejection_reason").HasMaxLength(500);
            e.HasOne<DomainServiceRequest>().WithMany().HasForeignKey(x => x.ServiceRequestId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ServiceRequestId, "ix_quotes_service_request");
            e.HasIndex(x => x.ServiceRequestId, "ux_quotes_pending").IsUnique().HasFilter("status = 'pendiente'");
            e.HasIndex(x => x.ServiceRequestId, "ux_quotes_accepted").IsUnique().HasFilter("status = 'aceptada'");
            // Vencimiento (RN-Q6): el barrido busca las pendientes por fecha de vencimiento.
            e.HasIndex(x => x.ExpiresAt, "ix_quotes_pending_expires").HasFilter("status = 'pendiente'");
        });

        // Evidencias (DD 5.10, RF-15). RLS y los permisos los agrega la migración Evidencias; file_url es la clave del objeto
        // en Garage (RN-E3).
        modelBuilder.Entity<ServiceEvidence>(e =>
        {
            e.ToTable("service_evidence");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ServiceRequestId).HasColumnName("service_request_id");
            e.Property(x => x.TechnicianId).HasColumnName("technician_id");
            e.Property(x => x.FileUrl).HasColumnName("file_url").HasMaxLength(500);
            e.Property(x => x.UploadedAt).HasColumnName("uploaded_at");
            e.HasOne<DomainServiceRequest>().WithMany().HasForeignKey(x => x.ServiceRequestId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ServiceRequestId, "ix_service_evidence_service_request");
        });

        modelBuilder.Entity<CategoryReplica>(e =>
        {
            e.ToTable("service_request_categories");
            e.HasKey(x => x.CategoryId);
            e.Property(x => x.CategoryId).HasColumnName("category_id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(100);
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(255);
            e.Property(x => x.Active).HasColumnName("active");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<OutboxEventRecord>(e =>
        {
            e.ToTable("outbox_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.AggregateId).HasColumnName("aggregate_id");
            e.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(100);
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.PublishedAt).HasColumnName("published_at");
            e.Property(x => x.Attempts).HasColumnName("attempts");
            e.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_outbox_events_pending").HasFilter("published_at IS NULL");
        });

        modelBuilder.Entity<ProcessedEventRecord>(e =>
        {
            e.ToTable("processed_events");
            e.HasKey(x => x.EventId);
            e.Property(x => x.EventId).HasColumnName("event_id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(100);
            e.Property(x => x.ProcessedAt).HasColumnName("processed_at");
        });

        // Auditoría propia de ServiceRequest (DD 5.14, v5.3): con tenant y RLS; el rol de la aplicación solo inserta y lee (RN-A1).
        modelBuilder.Entity<AuditLogRecord>(e =>
        {
            e.ToTable("audit_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
            e.Property(x => x.Action).HasColumnName("action").HasMaxLength(100);
            e.Property(x => x.TargetId).HasColumnName("target_id");
            e.Property(x => x.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => new { x.TenantId, x.CreatedAt }).HasDatabaseName("ix_audit_logs_tenant_created");
        });

        // Copia de solo lectura del catálogo de Matching (P-09): igual para todos los tenants, sin tenant_id ni RLS.
        modelBuilder.Entity<LocalityRecord>(e =>
        {
            e.ToTable("localities");
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasColumnName("code").HasMaxLength(10).ValueGeneratedNever();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(60);
            e.Property(x => x.Area).HasColumnName("area").HasColumnType("geometry(MultiPolygon,4326)");
            e.HasIndex(x => x.Name).IsUnique().HasDatabaseName("ux_localities_name");
        });
    }
}

/// <summary>Fila de <c>localities</c> (copia de DD 5.18, P-09); la carga una migración y la aplicación solo la lee.</summary>
public sealed class LocalityRecord
{
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public MultiPolygon Area { get; set; } = null!;
}

/// <summary>Fila de <c>outbox_events</c> (DD 5.15).</summary>
public sealed class OutboxEventRecord
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid AggregateId { get; set; }

    public string EventType { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public int Attempts { get; set; }
}

/// <summary>Fila de <c>processed_events</c> (DD 5.16).</summary>
public sealed class ProcessedEventRecord
{
    public Guid EventId { get; set; }

    public Guid TenantId { get; set; }

    public string EventType { get; set; } = string.Empty;

    public DateTimeOffset ProcessedAt { get; set; }
}

/// <summary>Fila de <c>audit_logs</c> de ServiceRequest (DD 5.14, v5.3).</summary>
public sealed class AuditLogRecord
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid? ActorUserId { get; set; }

    public string Action { get; set; } = string.Empty;

    public Guid? TargetId { get; set; }

    public string? Metadata { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}