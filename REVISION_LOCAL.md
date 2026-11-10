# Revisión de DocParse V1 local

Abrir http://127.0.0.1:5173/. La cuenta personal existente y sus documentos se conservan. Las pruebas integrales usan bases temporales.

1. **Alta de negocio:** en login, «Crear negocio». Elegir nombre, correo, usuario y contraseña, sin invitación. En http://127.0.0.1:8025/ abrir la confirmación y luego iniciar sesión. La cuenta es Admin solo de su negocio.
2. **Asistente:** crear tarjeta y editar nombre/bienvenida/acento/logo. El indicador superior muestra sincronización automática. En móvil se ve una columna.
3. **Conocimiento:** crear base, renombrar con lápiz y cargar PDF/imagen desde selector o arrastrando. La recepción termina antes del procesamiento; se puede seguir editando. Un duplicado reutiliza el documento; para fallidos usar Reintentar. Abrir texto, original y CSV cuando esté listo.
4. **Bases compartidas:** vincular base completa. Checkboxes seleccionan para «Quitar seleccionadas»: solo desvincula del asistente. El nombre despliega documentos.
5. **Accesos:** agregar correo, este asistente o todos los actuales. Mailpit recibe invitación. La persona elige credenciales o acepta con cuenta existente, confirma correo y solo accede al chat asignado. Cancelar/revocar impide recuperar grants mediante invitaciones antiguas.
6. **Chat:** preguntar por datos, revisar citas. Enter envía, Shift+Enter agrega línea. El botón junto al nombre limpia el contexto del asistente actual.
7. **API:** crear clave en cuarta pestaña, copiar secreto una vez, probar ejemplo desde servidor. Rotar/revocar invalida la anterior. No permite administrar ni descargar documentos.
8. **Widget:** habilitar y permitir http://127.0.0.1:5173. Usar snippet o http://127.0.0.1:5173/widget-demo.html#bot=ID_DEL_BOT. Mismo motor y bases; cambiar configuración invalida sesiones. No insertar API keys en navegador.
9. **Cuenta → Consumo y créditos:** incluidos/usados/reservados/restantes y desglose; tarifas de simulación. El plan cambia el próximo ciclo. Sin cobros.
10. **Calculadora de costos:** tarifas y escenarios propios, almacenamiento/egress/procesamiento. Previsión que no modifica créditos ni precios.
11. **Papelera:** mover documento/asistente y restaurar desde Cuenta. Una base debe desvincularse de todos los asistentes para borrarse. Tras 30 días se purga. Restaurar bot no reactiva claves/widget.

Docker local: PostgreSQL, Azurite, Ollama, Fyr Usage y Mailpit. Sin publicación ni aprovisionamiento Azure. Las tarifas de prueba no son una oferta comercial.

