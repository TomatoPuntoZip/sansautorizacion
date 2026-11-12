# SansAutorization

Sitio y API para solicitar gestión de trámites ante la EPS (autorizaciones y reclamaciones de medicamentos).

## Requisitos
- .NET SDK 10
- PostgreSQL 18 (o compatible)

## Configuración
1. Crear el usuario y la base de datos en PostgreSQL:
   - Usuario: `sansauto_app`
   - Base de datos: `sansautorizacion`, con `sansauto_app` como dueño.
2. Guardar la cadena de conexión fuera del código (no en `appsettings.json`):

   ```
   dotnet user-secrets set "ConnectionStrings:Postgres" "Host=localhost;Port=5432;Database=sansautorizacion;Username=sansauto_app;Password=TU_CONTRASENA"
   ```

## Ejecutar
```
dotnet run
```
Abrir la dirección que muestre la consola (por ejemplo `http://localhost:5000`).

## Estructura
- `Program.cs`: API y creación automática de la tabla `solicitudes`.
- `wwwroot/index.html`: página pública y formulario.

## Advertencia
Este proyecto guarda datos de salud. Antes de usarlo con personas reales faltan: HTTPS, acceso protegido para la gestora y copias de seguridad.
