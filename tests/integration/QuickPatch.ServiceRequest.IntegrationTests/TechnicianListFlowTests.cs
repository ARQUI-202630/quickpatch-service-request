using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using QuickPatch.ServiceRequest.IntegrationTests.Support;
using QuickPatch.ServiceRequest.UnitTests.Support;

namespace QuickPatch.ServiceRequest.IntegrationTests;

/// <summary>
/// SCRUM-464 contra PostgreSQL real: el servicio corre como <c>service_request_app</c> bajo RLS, así que la lista del
/// técnico nunca trae solicitudes de otro tenant aunque tengan su mismo <c>technician_id</c>.
/// </summary>
[Collection(EnvironmentDefinition.Name)]
public class TechnicianListFlowTests(ServiceRequestEnvironment env)
{
    private Task InsertAsync(Guid id, Guid tenant, Guid technician, string status, int assignedMinute) =>
        env.ExecuteAdminAsync(
            "INSERT INTO service_requests (id, tenant_id, client_id, category_id, technician_id, description, location, address_text, status, created_at, assigned_at) " +
            $"VALUES ('{id}', '{tenant}', '{Guid.NewGuid()}', '{Guid.NewGuid()}', '{technician}', 'Fuga de agua en la cocina', " +
            "ST_SetSRID(ST_MakePoint(-74.063, 4.6486), 4326), 'Calle 63 # 9-45', " +
            $"'{status}', now(), now() + interval '{assignedMinute} minutes');");

    [Fact]
    public async Task Lista_SoloDelTecnicoYDelTenantDelToken_SinContacto()
    {
        var tenant = Guid.NewGuid();
        var otroTenant = Guid.NewGuid();
        var technician = Guid.NewGuid();
        var (antigua, reciente, pagada) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await InsertAsync(antigua, tenant, technician, "asignado", 1);
        await InsertAsync(reciente, tenant, technician, "asignado", 5);
        await InsertAsync(pagada, tenant, technician, "pagado", 3);
        await InsertAsync(Guid.NewGuid(), tenant, Guid.NewGuid(), "asignado", 2);
        await InsertAsync(Guid.NewGuid(), otroTenant, technician, "asignado", 9);

        using var http = env.App.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(technician, tenant, "tecnico"));

        using var todas = await http.GetAsync(new Uri("/v1/technicians/me/service-requests", UriKind.Relative));
        using var asignadas = await http.GetAsync(new Uri("/v1/technicians/me/service-requests?status=asignado&size=1", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, todas.StatusCode);
        var json = await todas.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, json.GetProperty("totalItems").GetInt32());
        Assert.Equal([reciente, pagada, antigua], json.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
        Assert.All(json.GetProperty("items").EnumerateArray(), x =>
        {
            Assert.False(x.TryGetProperty("addressText", out _));
            Assert.False(x.TryGetProperty("location", out _));
            Assert.False(x.TryGetProperty("client", out _));
        });

        var filtro = await asignadas.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, filtro.GetProperty("totalItems").GetInt32());
        Assert.Equal(reciente, Assert.Single(filtro.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }
}