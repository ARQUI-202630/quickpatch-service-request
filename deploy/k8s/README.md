# Despliegue de ServiceRequest en k3s

`service-request.yaml` crea el ConfigMap, el Deployment (con las migraciones como init container), el Service y el Ingress en el namespace `quickpatch`. Es igual en QA y en producción; solo cambian los secretos y la dirección de Kafka.

## Secreto `service-request-secretos`

Lo crea DevOps desde Ansible Vault. Nunca se guarda en el repositorio.

|Clave|Contenido|
|---|---|
|`migrator-connection`|Cadena de conexión con `service_request_migrator` (dueño de las tablas; solo para las migraciones)|
|`app-connection`|Cadena de conexión con `service_request_app` (sin `BYPASSRLS`)|
|`jwt-public-key`|Llave pública RSA de Identity en PEM, para validar los tokens (ADR-018)|
|`redis-password`|Clave del usuario `service-request` de Redis (`vault_redis_users.service_request` en producción, `vault_qa_redis_users.service_request` en QA). Desde SCRUM-488|

```bash
kubectl -n quickpatch create secret generic service-request-secretos \
  --from-literal=migrator-connection='Host=<vm-datos>;Port=5432;Database=db_service_request;Username=service_request_migrator;Password=<...>' \
  --from-literal=app-connection='Host=<vm-datos>;Port=5432;Database=db_service_request;Username=service_request_app;Password=<...>' \
  --from-file=jwt-public-key=identity-public.pem   --from-literal=redis-password='<...>'
```

## Primer despliegue

1. Base: `db_service_request` con dueño `service_request_migrator` y el rol `service_request_app` (Ansible). La extensión PostGIS debe existir antes de la primera migración (`postgis_databases` incluye `service_request`): `service_request_migrator` no puede crearla.
2. Reemplazar `<vm-mensajeria>` y `<vm-datos>` en el ConfigMap por las IP de Kafka y de la VM de datos del ambiente y `kubectl apply -f deploy/k8s/service-request.yaml`. El pod queda esperando la imagen (`:pendiente`).
3. Push a `release/*`: el pipeline publica la imagen y la fija en el Deployment; el init container aplica las migraciones.
4. Solo la primera vez, un administrador de la base ejecuta `db/roles.sql` y reinicia el servicio con `kubectl -n quickpatch rollout restart deployment/service-request`.

**Desde SCRUM-103** (`roles.sql` con `service_request_platform`, `Platform__ReadRole` en el ConfigMap y las rutas `/v1/admin/service-requests` y `/v1/platform/service-requests` del Ingress): volver a aplicar `db/roles.sql` (playbook `aplicar-roles-db.yml` de quickpatch, se puede repetir) y `kubectl apply -f deploy/k8s/service-request.yaml` antes del release. Sin el rol, la vista de plataforma responde 500 al adoptarlo.

**Desde SCRUM-488** (cotizaciones): `roles.sql` crea `service_request_scheduler` y le da `SELECT` solo sobre las columnas de referencia de `quotes`; el ConfigMap agrega `Quotes__Ttl`, `Quotes__SchedulerRole`, `Redis__Endpoint` y `Redis__User`, y el Secret la clave `redis-password`. Antes del release: agregar la clave al Secret (`kubectl -n quickpatch patch secret service-request-secretos --type merge -p '{"stringData":{"redis-password":"<...>"}}'`), `kubectl apply -f deploy/k8s/service-request.yaml` con `<vm-datos>` reemplazado, y después del release volver a aplicar `db/roles.sql` (`aplicar-roles-db.yml`). Sin el rol, el proceso programado registra un error en cada vuelta y las cotizaciones no vencen; la API funciona igual.
