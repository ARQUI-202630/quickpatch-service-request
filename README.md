# service-request

**Tecnología:** ASP.NET Core

## Responsabilidad

Ciclo de vida de solicitudes, cotización, estados, evidencia y calificación según ownership documentado.

## Reglas

- Mantener el ownership definido en DD/SDD.
- No escribir directamente en tablas de otros servicios.
- Publicar/consumir eventos únicamente mediante contratos versionados.
- Mantener aislamiento multi-tenant cuando corresponda.

## Estructura

Cuatro capas, según el SDD (secciones 6.3 y 6.5):

```text
QuickPatch.ServiceRequest.slnx
src/
  QuickPatch.ServiceRequest.Api/              Endpoints, validación de entrada y raíz de composición
  QuickPatch.ServiceRequest.Application/      Casos de uso, comandos y consultas
  QuickPatch.ServiceRequest.Domain/           Entidades, value objects y reglas de negocio (sin dependencias externas)
  QuickPatch.ServiceRequest.Infrastructure/   PostgreSQL, Kafka, Redis y adaptadores externos
tests/
  unit/QuickPatch.ServiceRequest.UnitTests/
  integration/QuickPatch.ServiceRequest.IntegrationTests/
```

Dependencias permitidas: `Api → Application → Domain`; `Infrastructure → Application, Domain`. `Api` referencia `Infrastructure` solo para registrar sus servicios.

## Desarrollo local

Requiere el SDK de .NET indicado en `global.json`.

```bash
dotnet restore
dotnet format --verify-no-changes   # lint, igual que el CI
dotnet build -c Release
dotnet test tests/unit/QuickPatch.ServiceRequest.UnitTests
dotnet test tests/integration/QuickPatch.ServiceRequest.IntegrationTests
dotnet run --project src/QuickPatch.ServiceRequest.Api
```

## Contenedor

- Imagen: `Dockerfile` en la raíz (multi-stage, usuario sin privilegios).
- Puerto: `8080`.
- Probes para k3s: `GET /health/live` (el proceso responde) y `GET /health/ready` (el servicio y sus dependencias están listos).
