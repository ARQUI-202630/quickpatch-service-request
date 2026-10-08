# Despliegue de ServiceRequest en k3s

`service-request.yaml` crea el ConfigMap, el Deployment (con las migraciones como init container), el Service y el Ingress en el namespace `quickpatch`. Es igual en QA y en producción; solo cambian los secretos y la dirección de Kafka.

## Secreto `service-request-secretos`

Lo crea DevOps desde Ansible Vault. Nunca se guarda en el repositorio.

|Clave|Contenido|
|---|---|
|`migrator-connection`|Cadena de conexión con `service_request_migrator` (dueño de las tablas; solo para las migraciones)|
|`app-connection`|Cadena de conexión con `service_request_app` (sin `BYPASSRLS`)|
|`jwt-public-key`|Llave pública RSA de Identity en PEM, para validar los tokens (ADR-018)|

```bash
kubectl -n quickpatch create secret generic service-request-secretos \
  --from-literal=migrator-connection='Host=<vm-datos>;Port=5432;Database=db_service_request;Username=service_request_migrator;Password=<...>' \
  --from-literal=app-connection='Host=<vm-datos>;Port=5432;Database=db_service_request;Username=service_request_app;Password=<...>' \
  --from-file=jwt-public-key=identity-public.pem
```

## Primer despliegue

1. Base: `db_service_request` con dueño `service_request_migrator` y el rol `service_request_app` (Ansible). La extensión PostGIS debe existir antes de la primera migración (`postgis_databases` incluye `service_request`): `service_request_migrator` no puede crearla.
2. Reemplazar `<vm-mensajeria>` en el ConfigMap por la IP de Kafka del ambiente y `kubectl apply -f deploy/k8s/service-request.yaml`. El pod queda esperando la imagen (`:pendiente`).
3. Push a `release/*`: el pipeline publica la imagen y la fija en el Deployment; el init container aplica las migraciones.
4. Solo la primera vez, un administrador de la base ejecuta `db/roles.sql` y reinicia el servicio con `kubectl -n quickpatch rollout restart deployment/service-request`.
