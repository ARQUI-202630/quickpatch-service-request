using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

using NetTopologySuite.Geometries;

using QuickPatch.ServiceRequest.Domain.Categories;
using QuickPatch.ServiceRequest.Domain.ServiceRequests;

using DomainServiceRequest = QuickPatch.ServiceRequest.Domain.ServiceRequests.ServiceRequest;

namespace QuickPatch.ServiceRequest.Infrastructure.Persistence;

/// <summary>Base de datos de ServiceRequest Service (DD, secciones 5.5, 5.15, 5.16, 5.17 y la copia de 5.18).</summary>
public sealed class ServiceRequestDbContext(DbContextOptions<ServiceRequestDbContext> options) : DbContext(options)
{
    public DbSet<DomainServiceRequest> ServiceRequests => Set<DomainServiceRequest>();

    public DbSet<CategoryReplica> Categories => Set<CategoryReplica>();

    public DbSet<OutboxEventRecord> OutboxEvents => Set<OutboxEventRecord>();

    public DbSet<ProcessedEventRecord> ProcessedEvents => Set<ProcessedEventRecord>();

    public DbSet<LocalityRecord> Localities => Set<LocalityRecord>();

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
            e.Property<DateTimeOffset?>("StartedAt").HasColumnName("started_at");
            e.Property<DateTimeOffset?>("CompletedAt").HasColumnName("completed_at");
            e.Property<DateTimeOffset?>("CancelledAt").HasColumnName("cancelled_at");
            e.Property<string?>("CancellationReason").HasColumnName("cancellation_reason").HasColumnType("text");
            e.HasIndex(x => new { x.TenantId, x.ClientId }).HasDatabaseName("ix_service_requests_tenant_client");
            // Historial del técnico (RF-17, DD 5: índice sobre technician_id y status).
            e.HasIndex(x => new { x.TenantId, x.TechnicianId, x.Status }).HasDatabaseName("ix_service_requests_tenant_technician_status");
            e.HasIndex(x => x.Location).HasDatabaseName("ix_service_requests_location").HasMethod("gist");
        });

        modelBuilder.Entity<CategoryReplica>(e =>
        {
            e.ToTable("service_request_categories");
            e.HasKey(x => x.CategoryId);
            e.Property(x => x.CategoryId).HasColumnName("category_id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(100);
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