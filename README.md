# SansAutorization

Página y API para gestionar trámites ante la EPS (autorizaciones y reclamaciones de medicamentos) en Neiva, Huila.

## Qué incluye
- **Página pública (`/`):** precios, formulario por pasos para solicitar el trámite, asistente de preguntas frecuentes y adjuntos (fórmulas, autorizaciones o negativas de la EPS).
- **Ver mi caso (`/seguimiento.html`):** el cliente consulta el avance con su código y los últimos 4 dígitos de su WhatsApp.
- **Panel de encargos (`/panel/`):** acceso con usuario y contraseña. Permite ver los casos por estado, cambiar el nivel, los minutos y los pagos, ver el historial de cada caso, agregar notas internas y abrir los archivos.
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

## Ejecutar
```
dotnet run --urls http://localhost:5050
```
Abrir `http://localhost:5050` para la página pública y `http://localhost:5050/panel/` para el panel.

## Estructura
- `Program.cs`: API, validación, seguimiento, historial, notas y creación automática de las tablas.
- `wwwroot/index.html`: página pública y formulario.
- `wwwroot/seguimiento.html`: consulta del avance por el cliente.
- `wwwroot/panel/index.html`: panel de la gestora.
- `wwwroot/privacidad.html`: tratamiento de datos.
- `docs/revision-legal-pendiente.md`: preguntas que debe revisar un abogado.

## Advertencia
Este proyecto guarda datos de salud. Antes de usarlo con personas reales faltan: HTTPS, cifrado de los archivos adjuntos, copias de seguridad y la revisión legal de `docs/revision-legal-pendiente.md`.
