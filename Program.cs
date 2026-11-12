using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;

// ===== Configuración =====
// La cadena de conexión y la contraseña del panel vienen de appsettings.json o de user-secrets.
// Nunca se escriben en el código.
var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 20L * 1024 * 1024);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Falta la cadena de conexión 'Postgres'.");
var adminUser = builder.Configuration["Admin:User"] ?? "gestora";
var adminPassword = builder.Configuration["Admin:Password"];

// Límites de los adjuntos (fórmulas, autorizaciones, negativas de la EPS)
const int MaxArchivos = 3;
const long MaxBytesPorArchivo = 5L * 1024 * 1024;

// Límite de intentos seguimiento: máximo 8 cada 15 minutos por dirección
const int MaxIntentosSeguimiento = 8;
var TiempoVentanaSeguimiento = TimeSpan.FromMinutes(15);
var intentosSeguimiento = new ConcurrentDictionary<string, List<DateTime>>();

// Estados válidos de un caso (en orden de avance)
var estadosValidos = new[] { "Recibido", "Documentos pendientes", "Radicado", "En espera", "Resuelto", "Rechazado" };

// Precio de cada nivel: el servidor lo fija para que nadie lo cambie desde el navegador
var preciosPorNivel = new Dictionary<string, int>
{
    ["Simple"] = 20000,
    ["Medio"] = 40000,
    ["Complejo"] = 70000,
};

var app = builder.Build();

// ===== Base de datos: crea o actualiza las tablas =====
await using (var conn = new NpgsqlConnection(connectionString))
{
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand("""
        CREATE TABLE IF NOT EXISTS solicitudes (
            id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            nombre            text        NOT NULL,
            telefono          text        NOT NULL,
            municipio         text        NOT NULL,
            eps               text        NOT NULL,
            tramite           text        NOT NULL,
            detalle           text,
            consentimiento    boolean     NOT NULL,
            estado            text        NOT NULL DEFAULT 'Recibido',
            creado_en         timestamptz NOT NULL DEFAULT now()
        );
        ALTER TABLE solicitudes ADD COLUMN IF NOT EXISTS nivel text;
        ALTER TABLE solicitudes ADD COLUMN IF NOT EXISTS precio integer;
        ALTER TABLE solicitudes ADD COLUMN IF NOT EXISTS minutos integer;
        ALTER TABLE solicitudes ADD COLUMN IF NOT EXISTS adelanto_pagado boolean NOT NULL DEFAULT false;
        ALTER TABLE solicitudes ADD COLUMN IF NOT EXISTS saldo_pagado boolean NOT NULL DEFAULT false;
        ALTER TABLE solicitudes ADD COLUMN IF NOT EXISTS actualizado_en timestamptz;
        ALTER TABLE solicitudes ADD COLUMN IF NOT EXISTS codigo_seguimiento text;
        UPDATE solicitudes SET codigo_seguimiento = upper(substr(md5(random()::text || id::text), 1, 8))
            WHERE codigo_seguimiento IS NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS ix_solicitudes_codigo ON solicitudes (codigo_seguimiento);
        CREATE TABLE IF NOT EXISTS adjuntos (
            id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            solicitud_id    uuid        NOT NULL REFERENCES solicitudes(id) ON DELETE CASCADE,
            nombre          text        NOT NULL,
            tipo            text        NOT NULL,
            contenido       bytea       NOT NULL,
            creado_en       timestamptz NOT NULL DEFAULT now()
        );
        CREATE TABLE IF NOT EXISTS historial (
            id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            solicitud_id    uuid        NOT NULL REFERENCES solicitudes(id) ON DELETE CASCADE,
            estado          text        NOT NULL,
            nota            text,
            creado_en       timestamptz NOT NULL DEFAULT now()
        );
        CREATE TABLE IF NOT EXISTS notas (
            id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            solicitud_id    uuid        NOT NULL REFERENCES solicitudes(id) ON DELETE CASCADE,
            texto           text        NOT NULL,
            creado_en       timestamptz NOT NULL DEFAULT now()
        );
        """, conn);
    await cmd.ExecuteNonQueryAsync();
}

// ===== Protección del panel =====
// Todo lo que empiece por /panel o /api/casos exige usuario y contraseña.
// Si no hay contraseña configurada, se bloquea por completo.
app.Use(async (context, next) =>
{
    var ruta = context.Request.Path.Value ?? "";
    if (ruta.StartsWith("/panel") || ruta.StartsWith("/api/casos"))
    {
        if (string.IsNullOrEmpty(adminPassword))
        {
            context.Response.StatusCode = 503;
            await context.Response.WriteAsync("El panel no está configurado. Falta Admin:Password.");
            return;
        }
        if (!EsAutorizado(context.Request.Headers.Authorization.ToString(), adminUser, adminPassword))
        {
            context.Response.StatusCode = 401;
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"SansAutorization\"";
            return;
        }
    }
    await next();
});

// ===== Archivos estáticos: página pública, seguimiento y panel =====
app.UseDefaultFiles();
app.UseStaticFiles();

// ===== API pública: guardar una solicitud con adjuntos (multipart/form-data) =====
app.MapPost("/api/solicitudes", async (HttpRequest request) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { errores = new[] { "El formulario no llegó en el formato esperado." } });

    var form = await request.ReadFormAsync();
    var dto = new SolicitudDto(
        form["nombre"].ToString(),
        form["telefono"].ToString(),
        form["municipio"].ToString(),
        form["eps"].ToString(),
        form["tramite"].ToString(),
        form["detalle"].ToString(),
        form["consentimiento"].ToString() == "true");

    var errores = Validar(dto);

    var archivos = form.Files.GetFiles("adjuntos").Where(f => f.Length > 0).ToList();
    if (archivos.Count > MaxArchivos)
        errores.Add($"Puede adjuntar hasta {MaxArchivos} archivos.");

    var adjuntos = new List<(string Nombre, string Tipo, byte[] Datos)>();
    foreach (var archivo in archivos.Take(MaxArchivos))
    {
        if (archivo.Length > MaxBytesPorArchivo)
        {
            errores.Add($"'{archivo.FileName}' supera los 5 MB.");
            continue;
        }
        await using var memoria = new MemoryStream();
        await archivo.CopyToAsync(memoria);
        var datos = memoria.ToArray();
        var tipo = DetectarTipo(datos);
        if (tipo is null)
            errores.Add($"'{archivo.FileName}' debe ser una imagen JPG o PNG, o un PDF.");
        else
            adjuntos.Add((Path.GetFileName(archivo.FileName), tipo, datos));
    }

    if (errores.Count > 0)
        return Results.BadRequest(new { errores });

    var codigo = NuevoCodigo();

    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    Guid id;
    await using (var cmd = new NpgsqlCommand("""
        INSERT INTO solicitudes (nombre, telefono, municipio, eps, tramite, detalle, consentimiento, codigo_seguimiento)
        VALUES (@nombre, @telefono, @municipio, @eps, @tramite, @detalle, @consentimiento, @codigo)
        RETURNING id;
        """, conn, tx))
    {
        cmd.Parameters.AddWithValue("nombre", dto.Nombre.Trim());
        cmd.Parameters.AddWithValue("telefono", new string(dto.Telefono.Where(char.IsDigit).ToArray()));
        cmd.Parameters.AddWithValue("municipio", dto.Municipio.Trim());
        cmd.Parameters.AddWithValue("eps", dto.Eps.Trim());
        cmd.Parameters.AddWithValue("tramite", dto.Tramite.Trim());
        cmd.Parameters.AddWithValue("detalle", string.IsNullOrWhiteSpace(dto.Detalle) ? DBNull.Value : dto.Detalle.Trim());
        cmd.Parameters.AddWithValue("consentimiento", dto.Consentimiento);
        cmd.Parameters.AddWithValue("codigo", codigo);
        id = (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    await using (var cmdHistorial = new NpgsqlCommand(
        "INSERT INTO historial (solicitud_id, estado, nota) VALUES (@id, 'Recibido', 'Solicitud recibida en la página.');", conn, tx))
    {
        cmdHistorial.Parameters.AddWithValue("id", id);
        await cmdHistorial.ExecuteNonQueryAsync();
    }

    foreach (var adjunto in adjuntos)
    {
        await using var cmdAdjunto = new NpgsqlCommand("""
            INSERT INTO adjuntos (solicitud_id, nombre, tipo, contenido)
            VALUES (@solicitud, @nombre, @tipo, @contenido);
            """, conn, tx);
        cmdAdjunto.Parameters.AddWithValue("solicitud", id);
        cmdAdjunto.Parameters.AddWithValue("nombre", adjunto.Nombre);
        cmdAdjunto.Parameters.AddWithValue("tipo", adjunto.Tipo);
        cmdAdjunto.Parameters.AddWithValue("contenido", adjunto.Datos);
        await cmdAdjunto.ExecuteNonQueryAsync();
    }

    await tx.CommitAsync();
    return Results.Json(new { codigo = FormatearCodigo(codigo), adjuntos = adjuntos.Count }, statusCode: 201);
});

// ===== Seguimiento público: código + últimos 4 dígitos del WhatsApp =====
// Solo muestra trámite, estado y fechas. Nunca muestra nombre, adjuntos ni notas internas.
app.MapPost("/api/seguimiento", async (HttpContext http, SeguimientoDto dto) =>
{
    var ip = http.Connection.RemoteIpAddress?.ToString() ?? "desconocida";
    var ahora = DateTime.UtcNow;
    var intentos = intentosSeguimiento.GetOrAdd(ip, _ => new List<DateTime>());
    lock (intentos)
    {
        intentos.RemoveAll(t => ahora - t > TiempoVentanaSeguimiento);
        if (intentos.Count >= MaxIntentosSeguimiento)
            return Results.StatusCode(429);
        intentos.Add(ahora);
    }

    var codigo = (dto.Codigo ?? "").Replace("-", "").Replace(" ", "").ToUpperInvariant();
    var ultimos = new string((dto.UltimosCuatro ?? "").Where(char.IsDigit).ToArray());
    if (codigo.Length != 8 || ultimos.Length != 4)
        return Results.NotFound(new { mensaje = "No encontramos un caso con esos datos." });

    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand("""
        SELECT json_build_object(
            'tramite', s.tramite,
            'estado', s.estado,
            'creadoEn', s.creado_en,
            'telefono', s.telefono,
            'historial', (SELECT COALESCE(json_agg(json_build_object('estado', h.estado, 'fecha', h.creado_en) ORDER BY h.creado_en), '[]'::json)
                          FROM historial h WHERE h.solicitud_id = s.id)
        )::text
        FROM solicitudes s WHERE s.codigo_seguimiento = @codigo;
        """, conn);
    cmd.Parameters.AddWithValue("codigo", codigo);
    var json = await cmd.ExecuteScalarAsync() as string;

    if (json is null)
        return Results.NotFound(new { mensaje = "No encontramos un caso con esos datos." });

    using var doc = System.Text.Json.JsonDocument.Parse(json);
    var telefono = doc.RootElement.GetProperty("telefono").GetString() ?? "";
    if (!telefono.EndsWith(ultimos))
        return Results.NotFound(new { mensaje = "No encontramos un caso con esos datos." });

    return Results.Content(
        $"{{\"tramite\":{System.Text.Json.JsonSerializer.Serialize(doc.RootElement.GetProperty("tramite").GetString())},\"estado\":{System.Text.Json.JsonSerializer.Serialize(doc.RootElement.GetProperty("estado").GetString())},\"creadoEn\":{doc.RootElement.GetProperty("creadoEn").GetRawText()},\"historial\":{doc.RootElement.GetProperty("historial").GetRawText()}}}",
        "application/json");
});

// ===== Panel: listar casos con sus adjuntos (una sola consulta) =====
app.MapGet("/api/casos", async () =>
{
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand("""
        SELECT COALESCE(json_agg(c), '[]'::json)::text FROM (
            SELECT s.id, s.codigo_seguimiento AS codigo, s.nombre, s.telefono, s.municipio, s.eps, s.tramite, s.detalle,
                   s.nivel, s.precio, s.minutos, s.estado,
                   s.adelanto_pagado AS "adelantoPagado",
                   s.saldo_pagado AS "saldoPagado",
                   s.creado_en AS "creadoEn",
                   COALESCE((SELECT json_agg(json_build_object('id', a.id, 'nombre', a.nombre, 'tipo', a.tipo)
                                             ORDER BY a.creado_en)
                             FROM adjuntos a WHERE a.solicitud_id = s.id), '[]'::json) AS adjuntos
            FROM solicitudes s
            ORDER BY s.creado_en DESC
        ) c;
        """, conn);
    var json = (string)(await cmd.ExecuteScalarAsync())!;
    return Results.Content(json, "application/json");
});

// ===== Panel: detalle de un caso (datos, historial, notas y adjuntos) =====
app.MapGet("/api/casos/{id:guid}", async (Guid id) =>
{
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand("""
        SELECT json_build_object(
            'id', s.id,
            'codigo', s.codigo_seguimiento,
            'nombre', s.nombre,
            'telefono', s.telefono,
            'municipio', s.municipio,
            'eps', s.eps,
            'tramite', s.tramite,
            'detalle', s.detalle,
            'nivel', s.nivel,
            'precio', s.precio,
            'minutos', s.minutos,
            'estado', s.estado,
            'adelantoPagado', s.adelanto_pagado,
            'saldoPagado', s.saldo_pagado,
            'creadoEn', s.creado_en,
            'historial', (SELECT COALESCE(json_agg(json_build_object('estado', h.estado, 'nota', h.nota, 'fecha', h.creado_en) ORDER BY h.creado_en), '[]'::json)
                          FROM historial h WHERE h.solicitud_id = s.id),
            'notas', (SELECT COALESCE(json_agg(json_build_object('texto', n.texto, 'fecha', n.creado_en) ORDER BY n.creado_en), '[]'::json)
                      FROM notas n WHERE n.solicitud_id = s.id),
            'adjuntos', (SELECT COALESCE(json_agg(json_build_object('id', a.id, 'nombre', a.nombre, 'tipo', a.tipo) ORDER BY a.creado_en), '[]'::json)
                         FROM adjuntos a WHERE a.solicitud_id = s.id)
        )::text
        FROM solicitudes s WHERE s.id = @id;
        """, conn);
    cmd.Parameters.AddWithValue("id", id);
    var json = await cmd.ExecuteScalarAsync() as string;
    return json is null ? Results.NotFound() : Results.Content(json, "application/json");
});

// ===== Panel: ver un adjunto =====
app.MapGet("/api/casos/{id:guid}/adjuntos/{adjuntoId:guid}", async (Guid id, Guid adjuntoId) =>
{
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand("""
        SELECT tipo, nombre, contenido FROM adjuntos
        WHERE id = @adjunto AND solicitud_id = @solicitud;
        """, conn);
    cmd.Parameters.AddWithValue("adjunto", adjuntoId);
    cmd.Parameters.AddWithValue("solicitud", id);

    await using var lector = await cmd.ExecuteReaderAsync();
    if (!await lector.ReadAsync())
        return Results.NotFound();

    var tipo = lector.GetString(0);
    var nombre = lector.GetString(1);
    var datos = (byte[])lector.GetValue(2);
    return Results.File(datos, tipo, nombre, enableRangeProcessing: false);
});

// ===== Panel: actualizar un caso =====
// Solo se cambian los campos que llegan. Cada cambio de estado queda en el historial.
app.MapPatch("/api/casos/{id:guid}", async (Guid id, ActualizarCasoDto dto) =>
{
    var errores = new List<string>();
    if (dto.Estado is not null && !estadosValidos.Contains(dto.Estado)) errores.Add("Estado no válido.");
    if (dto.Nivel is not null && !preciosPorNivel.ContainsKey(dto.Nivel)) errores.Add("Nivel no válido.");
    if (dto.Minutos is < 0) errores.Add("Los minutos no pueden ser negativos.");
    if (dto.Nota is not null && dto.Nota.Length > 500) errores.Add("La nota es demasiado larga.");
    if (errores.Count > 0) return Results.BadRequest(new { errores });

    var cambios = new List<string>();
    var cmd = new NpgsqlCommand();
    if (dto.Estado is not null) { cambios.Add("estado = @estado"); cmd.Parameters.AddWithValue("estado", dto.Estado); }
    if (dto.Nivel is not null)
    {
        cambios.Add("nivel = @nivel");
        cambios.Add("precio = @precio");
        cmd.Parameters.AddWithValue("nivel", dto.Nivel);
        cmd.Parameters.AddWithValue("precio", preciosPorNivel[dto.Nivel]);
    }
    if (dto.Minutos is not null) { cambios.Add("minutos = @minutos"); cmd.Parameters.AddWithValue("minutos", dto.Minutos.Value); }
    if (dto.AdelantoPagado is not null) { cambios.Add("adelanto_pagado = @adelanto"); cmd.Parameters.AddWithValue("adelanto", dto.AdelantoPagado.Value); }
    if (dto.SaldoPagado is not null) { cambios.Add("saldo_pagado = @saldo"); cmd.Parameters.AddWithValue("saldo", dto.SaldoPagado.Value); }

    if (cambios.Count == 0 && string.IsNullOrWhiteSpace(dto.Nota)) return Results.BadRequest(new { errores = new[] { "No hay cambios que guardar." } });

    cambios.Add("actualizado_en = now()");
    cmd.CommandText = $"UPDATE solicitudes SET {string.Join(", ", cambios)} WHERE id = @id;";
    cmd.Parameters.AddWithValue("id", id);

    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    // Estado anterior, para saber si cambió
    string? estadoAnterior;
    await using (var cmdEstado = new NpgsqlCommand("SELECT estado FROM solicitudes WHERE id = @id FOR UPDATE;", conn, tx))
    {
        cmdEstado.Parameters.AddWithValue("id", id);
        estadoAnterior = await cmdEstado.ExecuteScalarAsync() as string;
    }
    if (estadoAnterior is null) return Results.NotFound();

    cmd.Connection = conn;
    cmd.Transaction = tx;
    await cmd.ExecuteNonQueryAsync();

    var estadoNuevo = dto.Estado ?? estadoAnterior;
    if (estadoNuevo != estadoAnterior || (dto.Nota is not null && dto.Nota.Trim().Length > 0))
    {
        await using var cmdHistorial = new NpgsqlCommand(
            "INSERT INTO historial (solicitud_id, estado, nota) VALUES (@id, @estado, @nota);", conn, tx);
        cmdHistorial.Parameters.AddWithValue("id", id);
        cmdHistorial.Parameters.AddWithValue("estado", estadoNuevo);
        cmdHistorial.Parameters.AddWithValue("nota", string.IsNullOrWhiteSpace(dto.Nota) ? DBNull.Value : dto.Nota.Trim());
        await cmdHistorial.ExecuteNonQueryAsync();
    }

    await tx.CommitAsync();
    return Results.NoContent();
});

// ===== Panel: agregar una nota interna (solo la gestora la ve) =====
app.MapPost("/api/casos/{id:guid}/notas", async (Guid id, NotaDto dto) =>
{
    var texto = (dto.Texto ?? "").Trim();
    if (texto.Length == 0) return Results.BadRequest(new { errores = new[] { "La nota está vacía." } });
    if (texto.Length > 1000) return Results.BadRequest(new { errores = new[] { "La nota es demasiado larga." } });

    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand("""
        INSERT INTO notas (solicitud_id, texto)
        SELECT id, @texto FROM solicitudes WHERE id = @id;
        """, conn);
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("texto", texto);
    var filas = await cmd.ExecuteNonQueryAsync();
    return filas == 0 ? Results.NotFound() : Results.Json(new { ok = true }, statusCode: 201);
});

app.Run();

// ===== Código de seguimiento: 8 caracteres sin letras o números que se confunden (0/O, 1/I/L) =====
static string NuevoCodigo()
{
    const string alfabeto = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    var chars = new char[8];
    for (var i = 0; i < chars.Length; i++)
        chars[i] = alfabeto[RandomNumberGenerator.GetInt32(alfabeto.Length)];
    return new string(chars);
}

static string FormatearCodigo(string codigo) => $"{codigo[..4]}-{codigo[4..]}";

// ===== Validación de la solicitud pública =====
static List<string> Validar(SolicitudDto dto)
{
    var errores = new List<string>();
    if (string.IsNullOrWhiteSpace(dto.Nombre)) errores.Add("Falta el nombre.");
    if (dto.Nombre?.Length > 120) errores.Add("El nombre es demasiado largo.");

    var digitos = new string((dto.Telefono ?? "").Where(char.IsDigit).ToArray());
    if (digitos.Length != 10) errores.Add("El WhatsApp debe tener 10 dígitos.");

    if (string.IsNullOrWhiteSpace(dto.Municipio)) errores.Add("Falta el municipio.");
    if (string.IsNullOrWhiteSpace(dto.Eps)) errores.Add("Falta la EPS.");
    if (string.IsNullOrWhiteSpace(dto.Tramite)) errores.Add("Falta el tipo de trámite.");
    if (dto.Detalle?.Length > 500) errores.Add("El detalle es demasiado largo.");

    if (!dto.Consentimiento) errores.Add("Se requiere la autorización para el uso de datos.");
    return errores;
}

// ===== Tipo real del archivo según sus primeros bytes (no según el nombre) =====
static string? DetectarTipo(byte[] d)
{
    if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
    if (d.Length >= 4 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "image/png";
    if (d.Length >= 4 && d[0] == 0x25 && d[1] == 0x50 && d[2] == 0x44 && d[3] == 0x46) return "application/pdf";
    return null;
}

// ===== Comprobación de usuario y contraseña (Basic) con comparación segura =====
static bool EsAutorizado(string encabezado, string usuario, string contrasena)
{
    if (!encabezado.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;

    string decodificado;
    try { decodificado = Encoding.UTF8.GetString(Convert.FromBase64String(encabezado["Basic ".Length..])); }
    catch (FormatException) { return false; }

    var separador = decodificado.IndexOf(':');
    if (separador < 0) return false;

    var usuarioOk = CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(decodificado[..separador]), Encoding.UTF8.GetBytes(usuario));
    var contrasenaOk = CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(decodificado[(separador + 1)..]), Encoding.UTF8.GetBytes(contrasena));
    return usuarioOk & contrasenaOk;
}

// Datos que llegan del formulario público
public record SolicitudDto(
    string Nombre,
    string Telefono,
    string Municipio,
    string Eps,
    string Tramite,
    string? Detalle,
    bool Consentimiento);

// Datos para consultar el seguimiento
public record SeguimientoDto(string? Codigo, string? UltimosCuatro);

// Cambios que puede hacer la gestora sobre un caso
public record ActualizarCasoDto(
    string? Estado,
    string? Nivel,
    int? Minutos,
    bool? AdelantoPagado,
    bool? SaldoPagado,
    string? Nota);

// Nota interna que solo ve la gestora
public record NotaDto(string? Texto);
