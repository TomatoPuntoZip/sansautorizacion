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
