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
|`POST /v1/service-requests` sin `location`: geocodifica `addressText` con el Nominatim propio, acotado al rectángulo de cobertura; si no la encuentra o no responde en `Geocoding:Timeout`, `422 direccion-no-encontrada` sin crear nada|`openapi/service-request.v1.yaml` 1.3.0|SCRUM-456 (ADR-024)|
|`POST /v1/service-requests/location-preview` (`cliente`, `empresa_contacto`): punto, localidad y dirección reconocida, sin crear nada; `422 direccion-no-encontrada` o `422 ubicacion-fuera-de-cobertura` (fuera del rectángulo o de las 20 localidades)|`openapi/service-request.v1.yaml` 1.3.0|SCRUM-456 (ADR-024, P-09)|
|`GET /v1/service-requests/{id}`: consulta del cliente dueño (otro tenant o cliente → 404)|`openapi/service-request.v1.yaml`|SCRUM-27 / SCRUM-69|
|`GET /v1/service-requests/{id}` para el técnico asignado, solo entre `asignado` y `completado` (fuera de ese rango → 403, otro técnico → 404), con `assignedAt`, `technician` y `client` (nombre y teléfono, RN-SR13). El cliente recibe `technician` y `client` en null|`openapi/service-request.v1.yaml` 1.1.0|SCRUM-34 / SCRUM-91|
|`GET /v1/technicians/me/service-requests`: solicitudes asignadas al técnico del token, filtro por `status` y paginación, de la asignación más reciente a la más antigua; sin dirección, ubicación ni cliente (RN-SR13)|`openapi/service-request.v1.yaml` 1.2.0|SCRUM-34 / SCRUM-464|
|Nombre y teléfono desde Identity con la consulta interna por lote, reenviando el JWT del usuario; si Identity no responde en `Identity:Timeout`, el detalle sale con los nombres en null|`openapi/identity-internal.v1.yaml` 1.0.0|SCRUM-91|
|Réplica local de categorías desde `catalog.category-changed` (sin llamadas a Catalog, SAD 4.3)|`events/catalog.category-changed.v1.json`|SCRUM-27|
|Estado por los eventos de Matching: `matching.technician-assigned` (`buscando_tecnico` → `asignado`, guarda `technician_id` y `assigned_at`), `matching.no-technician-available` (`buscando_tecnico` → `en_espera`) y `matching.search-resumed` (`en_espera` → `buscando_tecnico`). Un evento más viejo que el último aplicado (`matching_updated_at`) se descarta, porque llegan por topics distintos (ADR-017)|`events/matching.*.v1.json` (`quickpatch-kafka` v0.3.0)|SCRUM-29 / SCRUM-460, SCRUM-91|

Reglas aplicadas (DD, sección 7.4): RN-SR1, RN-SR9 (cobertura de Bogotá, configurable en `Coverage`), RN-SR10 (categoría activa en el tenant) y RN-SR11 (longitudes).

## Seguridad y datos

- **Autenticación:** JWT RS256 de Identity con los claims `sub`, `tenant_id` y `role`. El servicio solo tiene la llave pública (`Jwt:PublicKeyPem`); sin llave, todas las peticiones protegidas responden 401. Crear solicitudes exige el rol `cliente`; consultarlas, `cliente` (dueño) o `tecnico` (asignado). El token se reenvía a Identity para leer nombres y nunca se registra en logs.
- **Multi-tenancy:** el tenant sale del token. Cada operación corre en una transacción que fija `app.current_tenant` y las tablas tienen RLS forzado (DD, sección 10.2); sin tenant fijado no se ve ni se escribe nada.
- **Outbox (ADR-007):** el evento se guarda en `outbox_events` en la misma transacción que la solicitud. Un publicador en segundo plano lo envía a Kafka con el rol `Outbox:PublisherRole` (`BYPASSRLS`) y marca `published_at`.
- **Idempotencia:** el consumidor registra cada `eventId` en `processed_events` en la misma transacción del cambio.
- **Geocodificación (ADR-024, K12):** la dirección solo va al Nominatim propio de la VM de datos y nunca se registra en logs. Nominatim no usa el número de casa de la nomenclatura de Bogotá: si la dirección completa no da resultado, se reintenta una vez con la vía sola (lo que va antes de `#` o de la primera coma), dentro del mismo tiempo de espera. Por eso la app muestra la vista previa para que el cliente confirme o mueva el pin.
- **Correlación:** `X-Correlation-Id` se acepta o se genera, se devuelve, va a los logs y viaja en el evento.

## Base de datos

1. Migraciones (EF Core, con el rol dueño de las tablas): `dotnet tool restore` y `dotnet ef database update --project src/QuickPatch.ServiceRequest.Infrastructure --connection "<cadena>"`.
2. Roles y permisos del DD 10.2: `db/roles.sql`, ejecutado por un administrador después de las migraciones. Las contraseñas se asignan aparte (`ALTER ROLE service_request_app LOGIN PASSWORD ...`).
3. `localities` (P-09) es una copia de solo lectura del catálogo de Matching (DD 5.18): la migración `LocalidadesBogota` la carga con `Persistence/Migrations/Sql/localities.sql`, que repite el `INSERT` de la migración `V3` de `quickpatch-matching` (capa IDECA). No tiene `tenant_id` ni RLS y `service_request_app` solo tiene `SELECT` (la migración lo concede si el rol ya existe). **Si la capa cambia, se actualizan las dos migraciones.**

## Configuración

|Clave|Para qué|
|---|---|
|`ConnectionStrings__ServiceRequest`|PostgreSQL con el usuario `service_request_app`|
|`Jwt__PublicKeyPem`, `Jwt__Issuer`, `Jwt__Audience`|Validación del token de Identity|
|`Kafka__BootstrapServers`|Kafka (VM6); vacío deshabilita el publicador y el consumidor|
|`Outbox__PublisherRole`|Rol `BYPASSRLS` del publicador|
|`Coverage__*`|Rectángulo del área de cobertura (RN-SR9)|
|`Identity__BaseUrl`, `Identity__Timeout`|Identity dentro del clúster (`http://identity.quickpatch.svc.cluster.local:8080`) y espera máxima de la consulta de nombres (2 s); vacío no consulta y los nombres salen en null|
|`Geocoding__NominatimUrl`, `Geocoding__Timeout`|Nominatim de la VM de datos (QA: `http://10.43.98.29:8088`, VM5) y espera máxima de la geocodificación, reintento incluido (3 s); vacía, toda solicitud sin `location` responde `422 direccion-no-encontrada`|

## Pruebas

- `tests/unit`: dominio, casos de uso y API completa con los puertos en memoria. La cobertura de este proyecto mide Domain, Application y Api (`coverlet.runsettings`).
- `tests/integration`: PostgreSQL 16 + PostGIS y Kafka reales con Testcontainers (requiere Docker): migraciones, roles, RLS, Outbox publicado según el contrato, consumidor idempotente y aislamiento entre tenants. El detalle del técnico asignado se prueba con un Identity falso por HTTP real, y la geocodificación con un Nominatim falso por HTTP real contra las 20 localidades cargadas por la migración.

## Contenedor

- Imagen: `Dockerfile` en la raíz (multi-stage, usuario sin privilegios).
- Puerto: `8080`.
- Probes para k3s: `GET /health/live` (el proceso responde) y `GET /health/ready` (el servicio y sus dependencias están listos).

## Despliegue

- `deploy/k8s/service-request.yaml`: ConfigMap, Deployment, Service e Ingress (`/v1/service-requests` y `/v1/technicians/me/service-requests`) para k3s. Las migraciones se aplican con un *migration bundle* de EF Core (`/app/efbundle`, incluido en la imagen) como init container, con el rol `service_request_migrator`.
- Secretos y primer despliegue: `deploy/k8s/README.md`.
