-- Roles y permisos de ServiceRequest Service (DD, sección 10.2).
-- Lo ejecuta un administrador de la base DESPUÉS de las migraciones (que crean las tablas y las políticas RLS).
-- Las contraseñas no van aquí: se asignan aparte con ALTER ROLE ... LOGIN PASSWORD, desde el vault.
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'service_request_app') THEN
        CREATE ROLE service_request_app NOLOGIN NOBYPASSRLS;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'service_request_outbox') THEN
        CREATE ROLE service_request_outbox NOLOGIN BYPASSRLS;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'service_request_platform') THEN
        CREATE ROLE service_request_platform NOLOGIN BYPASSRLS;
    END IF;
END
$$;

GRANT USAGE ON SCHEMA public TO service_request_app, service_request_outbox, service_request_platform;

-- Rol del servicio: todo bajo RLS.
GRANT SELECT, INSERT, UPDATE ON service_requests, service_request_categories TO service_request_app;
GRANT SELECT, INSERT ON processed_events TO service_request_app;
GRANT INSERT ON outbox_events TO service_request_app;
-- Copia del catálogo de localidades (P-09): igual para todos los tenants, sin RLS y de solo lectura.
GRANT SELECT ON localities TO service_request_app;

-- Rol del publicador del Outbox: solo lee y marca eventos; lo adopta el servicio con SET LOCAL ROLE.
GRANT SELECT, UPDATE ON outbox_events TO service_request_outbox;
GRANT service_request_outbox TO service_request_app WITH INHERIT FALSE, SET TRUE;

-- Rol de la vista de plataforma del panel (DD 10.2 y 10.4, v5.2): solo cuenta solicitudes de todos los tenants. Permiso por
-- columna: no puede leer descripción, dirección, ubicación ni personas. Lo adopta el servicio con SET LOCAL ROLE.
GRANT SELECT (tenant_id, status, created_at) ON service_requests TO service_request_platform;
GRANT service_request_platform TO service_request_app WITH INHERIT FALSE, SET TRUE;
