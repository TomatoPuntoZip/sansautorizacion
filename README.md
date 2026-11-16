# SansAutorization

Página y API para gestionar trámites ante la EPS (autorizaciones y reclamaciones de medicamentos) en Neiva, Huila.

## Qué incluye
- **Página pública (`/`):** formulario por pasos para solicitar el trámite, asistente de preguntas frecuentes y adjuntos (fórmulas, autorizaciones o negativas de la EPS). Las fotos se vuelven a guardar en el navegador: así se quitan los datos ocultos, como la ubicación del celular, y se reduce su tamaño.
- **Ver mi caso (`/seguimiento.html`):** el cliente consulta el avance con su código y los últimos 4 dígitos de su WhatsApp.
- **Panel de encargos (`/panel/`):** acceso con usuario y contraseña. Permite ver los casos por estado, cambiar el nivel, el precio de cada caso (mínimo $15.000), los minutos y los pagos, ver el historial de cada caso, agregar notas internas y abrir los archivos. La lista trae 200 casos a la vez; con «Cargar más casos» se piden los siguientes. Los cambios de una tarjeta se guardan solo al pulsar «Guardar cambios»; el estado se protege si otra persona lo cambió antes.
- **Cómo tratamos sus datos (`/privacidad.html`):** política de tratamiento de datos en lenguaje sencillo.

## Requisitos
- .NET SDK 10
- PostgreSQL 18 (o compatible)

## Configuración
1. Crear el usuario y la base de datos en PostgreSQL:
   - Usuario: `sansauto_app`
   - Base de datos: `sansautorizacion`, con `sansauto_app` como dueño.
2. Guardar la conexión y la contraseña del panel fuera del código, con `dotnet user-secrets`:

   ```
   dotnet user-secrets set "ConnectionStrings:Postgres" "Host=localhost;Port=5432;Database=sansautorizacion;Username=sansauto_app;Password=TU_CONTRASENA"
   dotnet user-secrets set "Admin:Password" "TU_CLAVE_DEL_PANEL"
   ```

   El usuario del panel es `gestora`. Si no se configura `Admin:Password`, el panel no abre.

   Otras opciones (en `appsettings.json` o en `user-secrets`):

   - `Admin:User`: usuario del panel. Por defecto, `gestora`.
   - `Auditoria:DiasRetencion`: días que se conserva el registro de auditoría. Por defecto, `180`. Lo debe confirmar el abogado (ver `docs/revision-legal-pendiente.md`).
   - `Proxy:IpsConfiables`: lista de IP de los proxies de confianza, si hay alguno delante del servidor. Vacía por defecto: entonces el servidor ignora las cabeceras `X-Forwarded-For` y usa la IP real de la conexión. Solo acepta direcciones IP (no rangos CIDR).

## Ejecutar
```
dotnet run --urls http://localhost:5050
```
Abrir `http://localhost:5050` para la página pública y `http://localhost:5050/panel/` para el panel.

## Límites de uso
El servidor frena los abusos con límites que viven en memoria; se reinician al reiniciar el servidor:

- **Panel:** 10 contraseñas incorrectas por IP en 15 minutos. Después, la IP espera 15 minutos. Con la contraseña correcta siempre se entra.
- **Formulario:** 20 solicitudes por IP por hora y 100 MB de adjuntos por IP por hora.
- **Seguimiento:** 30 consultas por IP cada 15 minutos; 10 consultas de una misma IP sobre un mismo código por hora; y 50 consultas sobre un mismo código por hora, como tope contra la adivinación del código.

Las redes compartidas (una oficina o una zona rural con una sola IP) pueden agrupar a varias personas, por eso los límites por IP son holgados.

## Estructura
- `Program.cs`: API, validación, seguimiento, historial, notas y creación automática de las tablas.
- `wwwroot/index.html`: página pública y formulario.
- `wwwroot/seguimiento.html`: consulta del avance por el cliente.
- `wwwroot/panel/index.html`: panel de la gestora.
- `wwwroot/privacidad.html`: tratamiento de datos.
- `docs/revision-legal-pendiente.md`: preguntas que debe revisar un abogado.

## Advertencia
Este proyecto guarda datos de salud. Antes de usarlo con personas reales faltan: HTTPS, cifrado de los archivos adjuntos, copias de seguridad y la revisión legal de `docs/revision-legal-pendiente.md`.
