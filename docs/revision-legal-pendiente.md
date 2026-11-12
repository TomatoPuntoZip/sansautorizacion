# Revisión legal pendiente

Este documento reúne lo que debe confirmar un abogado antes de usar el servicio con personas reales. Mientras tanto, la página de privacidad se mantiene con afirmaciones generales.

## Preguntas para el abogado
1. **Plazo de conservación:** ¿cuánto tiempo debemos guardar las solicitudes, los adjuntos y el historial de un trámite de salud?
2. **Acceso a los datos:** ¿basta con que solo la gestora y el administrador los vean, o debe haber controles adicionales?
3. **Derechos del titular:** ¿es suficiente el WhatsApp para pedir ver o borrar datos, o debe haber un canal formal por escrito?
4. **Autorización para datos de salud:** ¿la casilla de consentimiento actual es suficiente, o hace falta una autorización específica?
5. **Actividad comercial:** ¿gestionar trámites ante la EPS con cobro requiere registro o alguna condición especial?
6. **Política de devolución:** ¿la redacción actual del adelanto no reembolsable es válida y clara para el cliente?
7. **Tratamiento de adjuntos:** ¿qué debemos hacer con las fotos y PDF de fórmulas y autorizaciones?

## Puntos de la página que hay que confirmar
- `privacidad.html`: "Solo los ven la gestora y el administrador" debe corresponder a la práctica real.
- `privacidad.html`: "No vendemos ni compartimos sus datos con empresas de publicidad" debe mantenerse solo si es cierto.
- `index.html`: el consentimiento que se muestra antes de enviar.

## Lo que ya está implementado
- Casilla de consentimiento obligatoria antes de enviar la solicitud.
- Acceso al panel con usuario y contraseña.
- Seguimiento público solo con código y los últimos 4 dígitos del WhatsApp, con límite de intentos.
- Los datos internos (notas y archivos) no se muestran en la página pública.

## Pendiente técnico antes de datos reales
- HTTPS.
- Cifrado de los archivos adjuntos.
- Copias de seguridad de la base de datos.
- Contraseñas nuevas (la de la base y la del panel aparecieron en el chat).

## Frases de la página que hoy no coinciden con el sistema
- `privacidad.html`: "Solo los ven quienes trabajan en su caso". El panel usa una sola cuenta compartida: quien tenga la contraseña ve todos los casos, y la auditoría registra la IP, no la persona.
- `privacidad.html`: "La revisamos y la eliminamos" y "pueden borrarlos por WhatsApp". El sistema no tiene borrado automático: solo se purga la auditoría a los 180 días. Si se promete borrar, hay que definir el procedimiento manual (quién lo hace y cómo se deja constancia).
- `privacidad.html`: no menciona que se registra la dirección IP de cada acceso al panel y cada envío (auditoría), ni que WhatsApp (Meta) recibe el número y el código cuando se escribe a la línea del servicio.

## Decisiones pendientes (no implementadas)
- **Verificación del WhatsApp:** hoy cualquiera puede registrar una solicitud con el número y el consentimiento de otra persona. Verificar el número cuesta dinero y depende de un proveedor; hay que decidir si se hace.
- **Topes globales:** los límites son por IP. Varias IP pueden llenar la base de adjuntos. Falta un tope diario global; hay que decidir la cifra.
- **Fuerza bruta en el seguimiento:** hoy el tope es de 50 consultas por hora por código (puede bloquear el código a un tercero durante una hora). La alternativa es bloquear tras 5 fallos por código, con el riesgo contrario. Hay que decidir.
- **Cuentas del panel:** conviene una cuenta por persona, para saber quién hizo cada cambio.
- **Metadatos de las fotos:** el formulario vuelve a dibujar todas las imágenes en el navegador, lo que quita la ubicación y el modelo del teléfono. Falta hacerlo también en el servidor.
- **Concurrencia:** el cambio de estado se protege si otra persona lo cambió antes (error 409). Los demás campos siguen siendo "gana el último que guarda".
- **Despliegue:** `AllowedHosts` está en `*`; las tablas se crean al arrancar con el mismo usuario de la aplicación; `Proxy:IpsConfiables` acepta solo IP, no rangos CIDR.
