# DocParse — módulos V1 en local

Fuente de producto: [documento de Drive](https://docs.google.com/document/d/1ao033DtGdlLEpfZSGLFziUoYwa5ztnBSh7mcjROCITE/edit), actualizado con las decisiones del chat el 9 de noviembre de 2026.

| Módulo | Base de datos, backend y frontend conectados |
| --- | --- |
| Access | Registro público del propietario, negocio propio y Admin tras verificar correo; login usuario/correo, recuperación, invitaciones para negocios existentes, reenvío/cancelación, miembros y protección del último Admin. Outbox de correo a Mailpit, plantillas EN/ES/VI. |
| Knowledge | Bases completas reutilizables, renombrado automático, vinculación/desvinculación, PDF/PNG/JPEG, texto/OCR local, procesamiento durable en segundo plano, reintentos, deduplicación, originales y CSV. |
| Bots | Tarjetas con Parámetros, Conocimiento, Accesos y API; logo privado, acento y bienvenida; sincronización compartida. Grants explícitos por correo e invitaciones ligadas a su generación; todos los bots actuales sin heredar futuros. |
| Chat | Un motor para dashboard, Viewer, API y widget; recuperación por organización/bases/perfil vectorial, citas por página, historial corto en memoria, Enter para enviar y borrado de contexto. |
| Channels (Bots/Chat) | Claves de conversación por asistente, secreto una vez, expiración/revocación/rotación; widget desactivado por defecto, orígenes exactos, sesión protegida, CSP y límites persistidos. |
| Usage | Fyr Usage V2 mediante ES256; reserva previa, tokens Ollama reales, outbox durable, límites por ciclo y desglose por bot/ingesta; consumo y calculadora en Cuenta. |
| Retención (cada módulo) | Papelera/restauración de 30 días, exclusión inmediata de búsqueda y acceso, purga y limpieza durable de archivos. Borrar bot conserva bases; una base debe desvincularse antes de borrarse. |

Un despliegue .NET modular, contratos entre funcionalidades y propiedad de datos por módulo. Fyr Usage es un servicio compartido existente con base separada; no se duplicó su cálculo de créditos.

## Límites del cierre local

- Tarifas y bolsas locales marcadas como **simulación**. Tokens reales; las cantidades no son precios comerciales ni costos medidos de GPU. Sin cobros.
- La medición habilitada admite Ollama. Un proveedor pago requiere adaptar y validar sus medidores. Sin fallback a nube.
- Historial del chat en memoria; no hay archivo durable de conversaciones.
- Mailpit recibe correo de prueba. Sin entrega real, Entra configurado ni publicación Azure.
- Antes de comercializar: parámetros económicos, correo real, licencias Excel/EPPlus, seguridad operativa, backups y entorno Azure. WhatsApp sigue en V2.

Revisión: [REVISION_LOCAL.md](REVISION_LOCAL.md). Preparación y validación: [V1_LOCAL.md](V1_LOCAL.md). Arquitectura: [back/ARCHITECTURE.md](back/ARCHITECTURE.md).

