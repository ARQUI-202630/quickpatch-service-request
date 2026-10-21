using System.Text.Json;

using QuickPatch.ServiceRequest.IntegrationTests.Support;

namespace QuickPatch.ServiceRequest.IntegrationTests;

/// <summary>
/// Eventos de Payments contra PostgreSQL y Kafka reales (SCRUM-115): <c>payment.approved</c> pasa la solicitud completada a
/// <c>pagado</c> bajo RLS y publica <c>service-request.status-changed</c>; <c>payment.rejected</c> la deja en <c>completado</c>.
/// </summary>
[Collection(EnvironmentDefinition.Name)]
public class PaymentFlowTests(ServiceRequestEnvironment env)
{
    private async Task<(Guid Id, Guid Tenant)> InsertCompletedAsync()
    {
        var (id, tenant) = (Guid.NewGuid(), Guid.NewGuid());
        await env.ExecuteAdminAsync(
            "INSERT INTO service_requests (id, tenant_id, client_id, category_id, technician_id, description, location, address_text, " +
            "status, created_at, assigned_at, started_at, completed_at) " +
            $"VALUES ('{id}', '{tenant}', '{Guid.NewGuid()}', '{Guid.NewGuid()}', '{Guid.NewGuid()}', 'Fuga de agua en la cocina', " +
            "ST_SetSRID(ST_MakePoint(-74.063, 4.6486), 4326), 'Calle 63 # 9-45', 'completado', now(), now(), now(), now());");
        return (id, tenant);
    }

    private static string Event(string type, Guid tenant, object data) => JsonSerializer.Serialize(new
    {
        eventId = Guid.NewGuid(),
        eventType = type,
        eventVersion = 1,
        occurredAt = DateTimeOffset.UtcNow,
        correlationId = Guid.NewGuid(),
        tenantId = tenant,
        producer = "payments-service",
        data,
    });

    [Fact]
    public async Task Rechazado_SigueCompletado_YAprobado_PasaAPagado()
    {
        var (id, tenant) = await InsertCompletedAsync();

        await env.PublishAsync("payment.rejected", id.ToString(), Event("payment.rejected", tenant, new
        {
            paymentId = Guid.NewGuid(),
            serviceRequestId = id,
            clientId = Guid.NewGuid(),
            technicianId = Guid.NewGuid(),
            amount = "185000.00",
            reasonCode = "fondos_insuficientes",
            rejectedAt = DateTimeOffset.UtcNow,
        }));
        await ServiceRequestEnvironment.WaitUntilAsync(
            async () => await env.ScalarAdminAsync<long>("SELECT count(*) FROM processed_events WHERE tenant_id = @t", ("t", tenant)) == 1,
            "ServiceRequest registra el rechazo");
        Assert.Equal("completado", await env.ScalarAdminAsync<string>("SELECT status FROM service_requests WHERE id = @id", ("id", id)));

        await env.PublishAsync("payment.approved", id.ToString(), Event("payment.approved", tenant, new
        {
            paymentId = Guid.NewGuid(),
            serviceRequestId = id,
            clientId = Guid.NewGuid(),
            companyId = (Guid?)null,
            technicianId = Guid.NewGuid(),
            amount = "185000.00",
            invoiceId = Guid.NewGuid(),
            approvedAt = DateTimeOffset.UtcNow,
        }));
        await ServiceRequestEnvironment.WaitUntilAsync(
            async () => await env.ScalarAdminAsync<string>("SELECT status FROM service_requests WHERE id = @id", ("id", id)) == "pagado",
            "la solicitud pasa a pagado");
        Assert.Equal(1L, await env.ScalarAdminAsync<long>(
            "SELECT count(*) FROM outbox_events WHERE aggregate_id = @id AND event_type = 'service-request.status-changed' " +
            "AND payload->'data'->>'status' = 'pagado'", ("id", id)));
    }
}