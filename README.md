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

## Capacidades implementadas

|Capacidad|Contrato|Historia|
|---|---|---|
|`POST /v1/service-requests`: crea la solicitud en `buscando_tecnico` y publica `service-request.created`|`openapi/service-request.v1.yaml`, `events/service-request.created.v1.json`|SCRUM-27 / SCRUM-69|
|`GET /v1/service-requests/{id}`: consulta del cliente dueño (otro tenant o cliente → 404)|`openapi/service-request.v1.yaml`|SCRUM-27 / SCRUM-69|
|Réplica local de categorías desde `catalog.category-changed` (sin llamadas a Catalog, SAD 4.3)|`events/catalog.category-changed.v1.json`|SCRUM-27|

Reglas aplicadas (DD, sección 7.4): RN-SR1, RN-SR9 (cobertura de Bogotá, configurable en `Coverage`), RN-SR10 (categoría activa en el tenant) y RN-SR11 (longitudes).

## Seguridad y datos

- **Autenticación:** JWT RS256 de Identity con los claims `sub`, `tenant_id` y `role`. El servicio solo tiene la llave pública (`Jwt:PublicKeyPem`); sin llave, todas las peticiones protegidas responden 401. Crear y consultar solicitudes exige el rol `cliente`.
- **Multi-tenancy:** el tenant sale del token. Cada operación corre en una transacción que fija `app.current_tenant` y las tablas tienen RLS forzado (DD, sección 10.2); sin tenant fijado no se ve ni se escribe nada.
- **Outbox (ADR-007):** el evento se guarda en `outbox_events` en la misma transacción que la solicitud. Un publicador en segundo plano lo envía a Kafka con el rol `Outbox:PublisherRole` (`BYPASSRLS`) y marca `published_at`.
- **Idempotencia:** el consumidor registra cada `eventId` en `processed_events` en la misma transacción del cambio.
- **Correlación:** `X-Correlation-Id` se acepta o se genera, se devuelve, va a los logs y viaja en el evento.

## Base de datos

1. Migraciones (EF Core, con el rol dueño de las tablas): `dotnet tool restore` y `dotnet ef database update --project src/QuickPatch.ServiceRequest.Infrastructure --connection "<cadena>"`.
2. Roles y permisos del DD 10.2: `db/roles.sql`, ejecutado por un administrador después de las migraciones. Las contraseñas se asignan aparte (`ALTER ROLE service_request_app LOGIN PASSWORD ...`).

## Configuración

|Clave|Para qué|
|---|---|
|`ConnectionStrings__ServiceRequest`|PostgreSQL con el usuario `service_request_app`|
|`Jwt__PublicKeyPem`, `Jwt__Issuer`, `Jwt__Audience`|Validación del token de Identity|
|`Kafka__BootstrapServers`|Kafka (VM6); vacío deshabilita el publicador y el consumidor|
|`Outbox__PublisherRole`|Rol `BYPASSRLS` del publicador|
|`Coverage__*`|Rectángulo del área de cobertura (RN-SR9)|

## Pruebas

- `tests/unit`: dominio, casos de uso y API completa con los puertos en memoria. La cobertura de este proyecto mide Domain, Application y Api (`coverlet.runsettings`).
- `tests/integration`: PostgreSQL 16 + PostGIS y Kafka reales con Testcontainers (requiere Docker): migraciones, roles, RLS, Outbox publicado según el contrato, consumidor idempotente y aislamiento entre tenants.

## Contenedor

- Imagen: `Dockerfile` en la raíz (multi-stage, usuario sin privilegios).
- Puerto: `8080`.
- Probes para k3s: `GET /health/live` (el proceso responde) y `GET /health/ready` (el servicio y sus dependencias están listos).

## Despliegue

- `deploy/k8s/service-request.yaml`: ConfigMap, Deployment, Service e Ingress (`C:/Program Files/Git/v1/service-requests`) para k3s. Las migraciones se aplican con un *migration bundle* de EF Core (`/app/efbundle`, incluido en la imagen) como init container, con el rol `service_request_migrator`.
- Secretos y primer despliegue: `deploy/k8s/README.md`.
