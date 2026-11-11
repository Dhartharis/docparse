# V1 local — entrega para revisión

Estado al 10 de noviembre de 2026. Fuente de producto: [DocParse en Drive](https://docs.google.com/document/d/1ao033DtGdlLEpfZSGLFziUoYwa5ztnBSh7mcjROCITE/edit), actualizado con las decisiones del chat. El alcance implementado es V1 local (Chat + Embed + API), sin publicación Azure, pagos, commits ni push.

## Funcionalidad conectada

- Propietario: registro público sin invitación, creación de negocio propio y Admin, confirmación de correo antes de login. Cuentas existentes pueden crear negocios separados.
- Acceso a negocios existentes: invitación por correo, credenciales elegidas por el destinatario, aceptación con cuenta existente, reenvío/cancelación y outbox durable. Mailpit recibe las pruebas; SMTP no implica entrega final.
- Permisos: roles Admin/Viewer, último Admin protegido, Viewers solo en asistentes asignados; grants pendientes ligados a invitación/generación. Todos los bots significa solo los que existen ahora.
- Tarjetas de asistentes: Parámetros/Conocimiento/Accesos/API, logo/acento/bienvenida y sincronización compartida; escritorio sin scroll externo y móvil en una columna.
- Conocimiento: bases completas reutilizables, renombrado automático, selección para desvincular sin borrar contenido compartido, carga y texto/OCR local en segundo plano, progreso durable, deduplicación SHA-256, Retry, originales y CSV.
- Chat: recuperación por tenant/bases/perfil, citas por documento/página, contexto corto en memoria, Enter/Shift+Enter y borrado de contexto; final JSON validado sin razonamiento interno.
- API: claves por asistente, solo conversación, secreto una vez, hash almacenado, expiración, rotación y revocación.
- Widget: opt-in del Admin, orígenes exactos, CSP, sesiones de 30 minutos, límites persistidos y mismo ChatScreen/QueryService; página de prueba local.
- Papelera: 30 días, restauración, exclusión inmediata de acceso/retrieval y purga durable de metadatos/vectores/archivos. Borrar bot conserva bases y revoca canales. Base vinculada requiere desvincularse primero.
- Consumo: Fyr Usage V2 como único cálculo de créditos, ES256, reservas previas, tokens Ollama reales y liquidación idempotente mediante outbox; desglose bot/ingesta, límites y planes por ciclo. Calculadora de escenarios manual independiente del cálculo de créditos.

## Validación ejecutada

| Comando / recorrido | Resultado observado |
| --- | --- |
| dotnet build back/DocParse.csproj --no-restore | Correcto, 0 errores/advertencias |
| python tests/check_backend_boundaries.py | PASS |
| npm --prefix front run build | Correcto; catálogos EN/ES/VI validados |
| npm --prefix front run lint | Correcto, sin advertencias en última ejecución |
| npm --prefix front test -- --workers=4 | 25 aprobadas; 4 recorridos reales omitidos deliberadamente aquí y ejecutados por separado |
| python tests/backend_integration.py --browser | PASS de backend y los 4 recorridos reales: acceso/recuperación, conocimiento/restauración, API/iframe y owner onboarding/Viewer |
| python tests/onboarding_integration.py | PASS: registro, idempotencia de negocio, invitaciones/generaciones, aceptación existente, reintentos SMTP exactos y recuperación tras reinicio |
| python tests/local_ai_integration.py --metered | PASS con Ollama y Fyr Usage reales: texto/OCR, tokens, reservas/límites, caída de servicio, reinicio y outbox |
| python tests/local_ai_integration.py --retention | PASS: dedup, bases vinculadas, restauración, denegación inmediata y purga real de vectores/Blob tras plazo forzado en fixture |
| python tests/usage_service_integration.py | 10 pruebas, 4 suites PostgreSQL aprobadas, incluyendo compatibilidad V1 y concurrencia/idempotencia V2 |
| Fyr Usage: npm run build / npm run lint | Correctos |
| Fyr Usage: npm audit --omit=dev | 0 vulnerabilidades runtime; audit completo conserva 14 en dependencias de desarrollo |
| git diff --check (ambos repositorios) | Sin errores de espacios; avisos de normalización LF/CRLF |
| Revisión real de Consumo/Calculadora en instancia principal | Escritorio 1440 y móvil 390, Axe WCAG sin infracciones, sin desbordamiento horizontal; cálculo manual verificó USD 1.611; sin cambiar plan/datos |
| Prueba personal anterior de chat con documento existente | Respuesta fyr-studio.com, 3.0725 créditos de simulación, reserva 0 y sin sincronización pendiente |

Las pruebas usan bases/contenedores temporales y proveedores locales/dobles. La cuenta y PDF personales se conservan. Los escenarios --metered y --retention se ejecutan por separado porque retención elimina su bot de fixture. Los recorridos visuales incluyen tamaños 320/390, 768 y 1440 y comprobaciones automáticas de accesibilidad; no equivalen a auditoría manual completa WCAG.

La última repetición de --metered también pasó los casos de caída antes de la primera reserva/ciclo, replay de una liquidación con acuse perdido sin doble descuento y resolución de un intento sin métricas cuando ya no hay cuota. Fallar antes de intentar reservar no deja outbox pendiente; los conflictos de integridad no se confunden con cuota agotada. La imagen API local se reconstruyó y quedó ejecutando ese ajuste. Dependencias de ambos clientes validadas con npm ls; API y frontend respondieron HTTP 200 al cierre.

## Preparación reproducible

Ver README.md para los pasos iniciales: dependencias en front y ../fyr-usage, levantar PostgreSQL/Azurite/Mailpit/Ollama, ejecutar node tools/setup-local-usage.mjs, construir front/dist y levantar Compose. Instalar una vez qwen3:4b-instruct y qwen3-embedding:0.6b. Para uso posterior basta docker compose up -d.

El script prepara fyr_usage_local y la clave ES256 privada en .local/usage-private.pem, ignorada por Git. Nunca imprimir/publicar esa clave. Los ciclos ya existentes conservan sus snapshots aunque se vuelvan a cargar tarifas. Persisten PostgreSQL, Azurite, modelos y claves de protección en volúmenes. No eliminar volúmenes al reiniciar.

Aplicación: http://127.0.0.1:5173/ · API: http://127.0.0.1:5128/swagger · Correo: http://127.0.0.1:8025/. Compose desactiva catálogos de traducción remotos y nube de Ollama.

## Archivos y decisiones materiales

- compose.yaml, tools/setup-local-usage.mjs y tools/usage-local.json: entorno local y configuración económica de simulación.
- back/Modules/Access: onboarding, invitaciones y correo durable.
- back/Modules/Bots y Chat: claves/canales, permisos y motor compartido.
- back/Modules/Knowledge y contratos Usage: cola de documentos, staging/dedup/retención y medición de proveedor.
- back/Modules/Usage: cliente ES256, reservas/outbox, panel de consumo y escenarios.
- Migraciones SQL 012–016: canales, contabilidad, retención/dedup, guardas de bases y onboarding/correo. Historial aplicado conservado.
- front/src/screens y components: flujos verticales, API/widget, consumo/calculadora/papelera y signup/invitación.
- front/localization/docparse/locales y pruebas frontend/backend: contratos y revisión visual EN/ES/VI.
- ../fyr-usage: migración 0003, contrato/controlador/servicio V2, adaptación de borrado de usuario, compatibilidad V1, pruebas y Dockerfile/dependencias runtime.
- README.md, back/README.md, back/ARCHITECTURE.md, MODULOS_LOCALES.md, REVISION_LOCAL.md y README de Fyr Usage: estado final y limitaciones. Drive conserva su estructura y decisiones de producto; se actualizó el bloque técnico y añadió la sección de validación local.

## Límites y trabajo antes de publicación

No hay bloqueo de infraestructura local para los recorridos probados. Esto no es una publicación ni certificación de producción.

1. Tarifas/bolsas actuales son simulación; no costos reales de GPU ni precios comerciales. El mecanismo de créditos ya usa Fyr Usage. Falta fijar parámetros comerciales y costos atribuibles reales antes de cobrar.
2. Metering habilitado admite Ollama; adaptadores pagos están bloqueados hasta implementar/validar sus mediciones. Almacenamiento/retrieval/egress/procesamiento en calculadora son previsiones manuales, no medición automática de infraestructura.
3. Mailpit solo prueba correo. Entrega real, secretos, HTTPS/orígenes, backups/restauración, observabilidad, roles mínimos y despliegue/migraciones Azure requieren configuración y aceptación en ese entorno.
4. Entra External ID no está integrado; las credenciales locales no se pueden prometer como exportables. Identidades externas deben mapearse de forma explícita a IDs estables.
5. Chat conserva contexto en memoria, no historial durable de conversaciones. Cambiar modelo de embeddings requiere reindexación explícita. OCR puede contener errores.
6. Revisar licencia EPPlus antes de comercializar Excel. Dependencias de desarrollo Fyr Usage aún tienen avisos de audit; no se forzó una actualización ajena del test runner.
7. No se borran archivos históricos desconocidos por barrido global: Azurite se comparte entre bases aisladas y no permite atribuirles propietario con seguridad. El staging nuevo sí permite limpieza durable.
8. WhatsApp es V2. Azure/free tier y sus costos se verifican antes del despliegue; no asumir cobertura gratuita de todos los servicios.

Recorrido de revisión: [REVISION_LOCAL.md](REVISION_LOCAL.md). Arquitectura: [back/ARCHITECTURE.md](back/ARCHITECTURE.md).

