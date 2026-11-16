using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;

// ===== Configuración =====
// La cadena de conexión y la contraseña del panel vienen de appsettings.json o de user-secrets. Nunca en el código.
var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 20L * 1024 * 1024);

// Solo se confía en las cabeceras reenviadas de los proxies listados en Proxy:IpsConfiables.
// Sin esa lista, la IP que cuenta es la de la conexión, y las cabeceras de cualquiera se ignoran.
var proxiesConfiables = builder.Configuration.GetSection("Proxy:IpsConfiables").Get<string[]>() ?? Array.Empty<string>();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownProxies.Clear();
    o.KnownIPNetworks.Clear();
    foreach (var texto in proxiesConfiables)
        if (IPAddress.TryParse(texto, out var ipConfiable)) o.KnownProxies.Add(ipConfiable);
});

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Falta la cadena de conexión 'Postgres'.");
var adminUser = builder.Configuration["Admin:User"] ?? "gestora";
var adminPassword = builder.Configuration["Admin:Password"];
// Días que se conserva la auditoría. Lo confirma el abogado (ver docs/revision-legal-pendiente.md).
var diasRetencionAuditoria = builder.Configuration.GetValue<int?>("Auditoria:DiasRetencion") ?? 180;

// Límites de los adjuntos (fórmulas, autorizaciones, negativas de la EPS)
const int MaxArchivos = 3;
const long MaxBytesPorArchivo = 5L * 1024 * 1024;

// Límites de uso. Cada consulta cuenta ANTES de tocar la base de datos, en un solo paso atómico.
// Las redes compartidas (oficinas, CGNAT rural) pueden agrupar a varias personas tras una misma IP: por eso los
// umbrales por IP son holgados y el control fuerte va por código, con un tope global.
var VentanaHora = TimeSpan.FromHours(1);
var VentanaLogin = TimeSpan.FromMinutes(15);
var VentanaSeguimientoIp = TimeSpan.FromMinutes(15);
const int MaxFallosLogin = 10;                    // contraseñas incorrectas del panel, por IP en 15 min
const int MaxEnviosPorIpHora = 20;                // solicitudes por IP y hora
const int MaxMbAdjuntosPorIpHora = 100;           // volumen de adjuntos por IP y hora
const int MaxConsultasSeguimientoIp = 30;         // consultas por IP en 15 min
const int MaxConsultasSeguimientoIpCodigo = 10;   // consultas de una IP sobre un mismo código, por hora
const int MaxConsultasSeguimientoCodigo = 50;     // consultas sobre un mismo código, por hora (tope anti fuerza bruta)

// Estados válidos de un caso (en orden de avance)
var estadosValidos = new[] { "Recibido", "Documentos pendientes", "Radicado", "En espera", "Resuelto", "Rechazado" };


var app = builder.Build();

// ===== Base de datos: crea o actualiza las tablas e índices =====
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
        CREATE INDEX IF NOT EXISTS ix_solicitudes_creado ON solicitudes (creado_en DESC, id DESC);
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
        CREATE TABLE IF NOT EXISTS auditoria (
            id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            creado_en       timestamptz NOT NULL DEFAULT now(),
            ip              text        NOT NULL,
            accion          text        NOT NULL,
            caso_id         uuid,
            detalle         text
        );
        CREATE INDEX IF NOT EXISTS ix_adjuntos_solicitud ON adjuntos (solicitud_id);
        CREATE INDEX IF NOT EXISTS ix_historial_solicitud ON historial (solicitud_id);
        CREATE INDEX IF NOT EXISTS ix_notas_solicitud ON notas (solicitud_id);
        CREATE INDEX IF NOT EXISTS ix_auditoria_fecha ON auditoria (creado_en);
        """, conn);
    await cmd.ExecuteNonQueryAsync();
}


// Retención de la auditoría: se purga al arrancar y cada 24 horas
await Auditoria.PurgarAntiguos(connectionString, diasRetencionAuditoria);
Mantenimiento.Iniciar(connectionString, diasRetencionAuditoria);

// ===== Cabeceras de seguridad =====
// No se usa CSP porque las páginas tienen scripts en línea; se añade cuando se separen.
// Sin proxies de confianza NO se activa: así nadie puede inventar su IP con una cabecera
if (proxiesConfiables.Length > 0) app.UseForwardedHeaders();

// La consulta de seguimiento es muy corta: un cuerpo grande se rechaza antes de leerlo
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/seguimiento", StringComparison.OrdinalIgnoreCase)
        && context.Request.ContentLength is > 4096L)
    {
        context.Response.StatusCode = 413;
        return;
    }
    await next();
});
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    var ruta = RutaNormalizada(context.Request.Path);
    if (ruta.StartsWith("/panel") || ruta.StartsWith("/api/casos") || ruta.StartsWith("/api/seguimiento"))
        headers["Cache-Control"] = "no-store";
    await next();
});

// ===== Protección del panel =====
// /panel y /api/casos exigen usuario y contraseña. Con la contraseña correcta se entra siempre;
// el bloqueo solo frena a quien falla, para que la gestora nunca quede fuera por su propio navegador.
app.Use(async (context, next) =>
{
    var ruta = RutaNormalizada(context.Request.Path); // minúsculas y sin barras repetidas: el enrutado no distingue mayúsculas
    if (!(ruta.StartsWith("/panel") || ruta.StartsWith("/api/casos")))
    {
        await next();
        return;
    }

    if (string.IsNullOrEmpty(adminPassword))
    {
        context.Response.StatusCode = 503;
        await context.Response.WriteAsync("El panel no está disponible en este momento.");
        return;
    }

    var autorizacion = context.Request.Headers.Authorization.ToString();
    if (EsAutorizado(autorizacion, adminUser, adminPassword))
    {
        await next();
        return;
    }

    // Sin cabecera de autorización es la primera visita del navegador, no un intento fallido
    if (autorizacion.Length == 0)
    {
        context.Response.StatusCode = 401;
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"SansAutorization\"";
        return;
    }

    var ip = Ip.Clave(context.Connection.RemoteIpAddress);
    var textoIp = Ip.Texto(context.Connection.RemoteIpAddress);

    if (Limitador.Bloqueado("login:" + ip, MaxFallosLogin, VentanaLogin))
    {
        // Se audita una sola vez por ventana, no en cada petición bloqueada
        if (Limitador.Permitir("auditoria-bloqueo:" + ip, 1, VentanaLogin))
            await Auditoria.Segura(connectionString, textoIp, "bloqueo_login", null, null);

        context.Response.Headers["Retry-After"] = ((int)VentanaLogin.TotalSeconds).ToString();
        context.Response.StatusCode = 429;
        await context.Response.WriteAsJsonAsync(new { errores = new[] { "Demasiados intentos fallidos. Espere 15 minutos." } });
        return;
    }

    Limitador.Fallar("login:" + ip);
    await Auditoria.Segura(connectionString, textoIp, "login_fallido", null, null);
    context.Response.StatusCode = 401;
    context.Response.Headers.WWWAuthenticate = "Basic realm=\"SansAutorization\"";
});

// ===== Archivos estáticos: página pública, seguimiento y panel =====
app.UseDefaultFiles();
app.UseStaticFiles();

// ===== API pública: guardar una solicitud con adjuntos (multipart/form-data) =====
app.MapPost("/api/solicitudes", async (HttpContext http) =>
{
    var ip = Ip.Clave(http.Connection.RemoteIpAddress);

    // Clave de envío: una por intento del formulario. Si el navegador reintenta un envío que ya se registró,
    // recibe la misma solicitud y no se crea otra.
    var claveEnvio = http.Request.Headers["Idempotency-Key"].ToString();
    if (claveEnvio.Length > 0)
    {
        if (!Envios.ClaveValida(claveEnvio))
            return Results.BadRequest(new { errores = new[] { "La clave del envío no es válida." } });
        if (Envios.Consultar(claveEnvio, out var codigoRegistrado, out var adjuntosRegistrados) == EstadoEnvio.Registrado)
            return Results.Json(new { codigo = codigoRegistrado, adjuntos = adjuntosRegistrados }, statusCode: 201);
    }

    if (!Limitador.Permitir("formulario:" + ip, MaxEnviosPorIpHora, VentanaHora))
        return Limitado(http, "Ha enviado varias solicitudes desde este equipo. Espere un rato o escríbanos por WhatsApp.", VentanaHora);

    if (!http.Request.HasFormContentType)
        return Results.BadRequest(new { errores = new[] { "El formulario no llegó en el formato esperado." } });

    IFormCollection form;
    try
    {
        form = await http.Request.ReadFormAsync();
    }
    catch (Exception ex) when (ex is BadHttpRequestException or InvalidDataException or IOException)
    {
        // Multipart dañado o cortado: se responde 400 y no 500
        return Results.BadRequest(new { errores = new[] { "El formulario llegó incompleto. Intente de nuevo." } });
    }

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

    // Volumen de adjuntos: se cobra por megabyte para que 20 envíos no puedan llenar el disco
    var megas = (int)Math.Ceiling(adjuntos.Sum(a => (long)a.Datos.Length) / 1048576.0);
    if (megas > 0 && !Limitador.Permitir("adjuntos:" + ip, MaxMbAdjuntosPorIpHora, VentanaHora, megas))
        return Limitado(http, "Ha subido demasiados archivos desde este equipo en la última hora. Escríbanos por WhatsApp.", VentanaHora);

    // Si la clave ya está en uso por otro intento que sigue guardando, este no guarda otra vez
    if (claveEnvio.Length > 0)
    {
        var reserva = Envios.Reservar(claveEnvio, out var codigoYaRegistrado, out var adjuntosYaRegistrados);
        if (reserva == EstadoEnvio.Registrado)
            return Results.Json(new { codigo = codigoYaRegistrado, adjuntos = adjuntosYaRegistrados }, statusCode: 201);
        if (reserva == EstadoEnvio.EnCurso)
            return Results.Json(new { errores = new[] { "Su solicitud todavía se está registrando. Espere un momento." } }, statusCode: 409);
    }

    // Colisión improbable del código de seguimiento: se reintenta con otro código (hasta tres intentos)
    for (var intento = 1; ; intento++)
    {
        var codigo = NuevoCodigo();
        try
        {
            await GuardarSolicitud(connectionString, dto, codigo, adjuntos, ip);
            var formateado = FormatearCodigo(codigo);
            if (claveEnvio.Length > 0) Envios.Confirmar(claveEnvio, formateado, adjuntos.Count);
            return Results.Json(new { codigo = formateado, adjuntos = adjuntos.Count }, statusCode: 201);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505" && intento < 3)
        {
            // Mismo código ya existente: se genera otro en la siguiente vuelta
        }
        catch
        {
            // Si el guardado falla, la clave queda libre para que el reintento pueda usarla
            if (claveEnvio.Length > 0) Envios.Liberar(claveEnvio);
            throw;
        }
    }
});

// ===== Seguimiento público: código + últimos 4 dígitos del WhatsApp =====
// Solo muestra trámite, estado y fechas. Cada consulta se cuenta antes de consultar la base.
app.MapPost("/api/seguimiento", async (HttpContext http, SeguimientoDto dto) =>
{
    var ip = Ip.Clave(http.Connection.RemoteIpAddress);
    var codigo = (dto.Codigo ?? "").Replace("-", "").Replace(" ", "").ToUpperInvariant();
    var ultimos = SoloDigitos(dto.UltimosCuatro ?? "");
    var noEncontrado = Results.NotFound(new { mensaje = "No encontramos un caso con esos datos." });

    // Un formato que no puede existir se responde de inmediato: no entra en los contadores ni toca la base
    if (codigo.Length != 8 || ultimos.Length != 4)
        return noEncontrado;

    if (!Limitador.Permitir("seguimiento-ip:" + ip, MaxConsultasSeguimientoIp, VentanaSeguimientoIp))
        return Limitado(http, "Ha hecho muchas consultas. Espere 15 minutos o escríbanos por WhatsApp.", VentanaSeguimientoIp);
    if (!Limitador.Permitir("seguimiento-ip-codigo:" + ip + ":" + codigo, MaxConsultasSeguimientoIpCodigo, VentanaHora))
        return Limitado(http, "Ha consultado este caso muchas veces. Espere un rato o escríbanos por WhatsApp.", VentanaHora);
    if (!Limitador.Permitir("seguimiento-codigo:" + codigo, MaxConsultasSeguimientoCodigo, VentanaHora))
        return Limitado(http, "Hay demasiadas consultas sobre este caso. Espere un rato o escríbanos por WhatsApp.", VentanaHora);

    // El teléfono se compara dentro de la misma consulta: no hay camino distinto según exista o no el código
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand("""
        SELECT json_build_object(
            'tramite', s.tramite,
            'estado', s.estado,
            'creadoEn', s.creado_en,
            'historial', (SELECT COALESCE(json_agg(json_build_object('estado', h.estado, 'fecha', h.creado_en) ORDER BY h.creado_en), '[]'::json)
                          FROM historial h WHERE h.solicitud_id = s.id)
        )::text
        FROM solicitudes s
        WHERE s.codigo_seguimiento = @codigo AND RIGHT(s.telefono, 4) = @ultimos;
        """, conn);
    cmd.Parameters.AddWithValue("codigo", codigo);
    cmd.Parameters.AddWithValue("ultimos", ultimos);
    var json = await cmd.ExecuteScalarAsync() as string;

    return json is null ? noEncontrado : Results.Content(json, "application/json");
});

// ===== Panel: listar casos (paginado: 200 por defecto, máximo 500) =====
// Se devuelve el total en la cabecera X-Total-Count para poder pedir las páginas siguientes con "desplazamiento".
app.MapGet("/api/casos", async (HttpContext http, int? limite, int? desplazamiento) =>
{
    var cantidad = Math.Clamp(limite ?? 200, 1, 500);
    var saltar = Math.Max(0, desplazamiento ?? 0);

    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();

    long total;
    await using (var cmdTotal = new NpgsqlCommand("SELECT count(*) FROM solicitudes;", conn))
        total = (long)(await cmdTotal.ExecuteScalarAsync())!;

    await using var cmd = new NpgsqlCommand("""
        SELECT COALESCE(json_agg(c ORDER BY c."creadoEn" DESC, c.id DESC), '[]'::json)::text FROM (
            SELECT s.id, s.codigo_seguimiento AS codigo, s.nombre, s.telefono, s.municipio, s.eps, s.tramite, s.detalle,
                   s.nivel, s.precio, s.minutos, s.estado,
                   s.adelanto_pagado AS "adelantoPagado",
                   s.saldo_pagado AS "saldoPagado",
                   s.creado_en AS "creadoEn",
                   COALESCE((SELECT json_agg(json_build_object('id', a.id, 'nombre', a.nombre, 'tipo', a.tipo)
                                             ORDER BY a.creado_en)
                             FROM adjuntos a WHERE a.solicitud_id = s.id), '[]'::json) AS adjuntos
            FROM solicitudes s
            ORDER BY s.creado_en DESC, s.id DESC
            LIMIT @cantidad OFFSET @saltar
        ) c;
        """, conn);
    cmd.Parameters.AddWithValue("cantidad", cantidad);
    cmd.Parameters.AddWithValue("saltar", saltar);
    var json = (string)(await cmd.ExecuteScalarAsync())!;

    if (!await Auditoria.Exigir(connectionString, Ip.Texto(http.Connection.RemoteIpAddress), "listar_casos", null,
            $"limite={cantidad};desplazamiento={saltar};total={total}"))
        return Results.StatusCode(503);

    http.Response.Headers["X-Total-Count"] = total.ToString();
    return Results.Content(json, "application/json");
});

// ===== Panel: detalle de un caso (datos, historial, notas y adjuntos) =====
app.MapGet("/api/casos/{id:guid}", async (Guid id, HttpContext http) =>
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
    if (json is null) return Results.NotFound();

    if (!await Auditoria.Exigir(connectionString, Ip.Texto(http.Connection.RemoteIpAddress), "ver_caso", id, null))
        return Results.StatusCode(503);
    return Results.Content(json, "application/json");
});

// ===== Panel: ver un adjunto =====
app.MapGet("/api/casos/{id:guid}/adjuntos/{adjuntoId:guid}", async (Guid id, Guid adjuntoId, HttpContext http) =>
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

    // La auditoría guarda el identificador del adjunto, nunca su nombre (puede llevar datos personales).
    // Si no se puede registrar, el archivo no se entrega.
    if (!await Auditoria.Exigir(connectionString, Ip.Texto(http.Connection.RemoteIpAddress), "ver_adjunto", id, adjuntoId.ToString()))
        return Results.StatusCode(503);

    return Results.File(datos, tipo, NombreDescarga(nombre, tipo), enableRangeProcessing: false);
});

// ===== Panel: actualizar un caso =====
// Solo se cambian los campos que llegan. El cambio, su historial y su auditoría van en una sola transacción.
app.MapPatch("/api/casos/{id:guid}", async (Guid id, ActualizarCasoDto dto, HttpContext http) =>
{
    var errores = new List<string>();
    if (dto.Estado is not null && !estadosValidos.Contains(dto.Estado)) errores.Add("Estado no válido.");
    if (dto.Nivel is not null && dto.Nivel is not ("Simple" or "Medio" or "Complejo")) errores.Add("Nivel no válido.");
    if (dto.Precio is not null && dto.Precio is < 15000 or > 10000000) errores.Add("El precio debe estar entre $15.000 y $10.000.000.");
    if (dto.Minutos is < 0 or > 100000) errores.Add("Los minutos deben estar entre 0 y 100.000.");
    if (dto.Nota is not null && dto.Nota.Length > 1000) errores.Add("La nota es demasiado larga.");
    if (errores.Count > 0) return Results.BadRequest(new { errores });

    var cambios = new List<string>();
    var camposCambiados = new List<string>();
    await using var cmd = new NpgsqlCommand();
    if (dto.Estado is not null) { cambios.Add("estado = @estado"); cmd.Parameters.AddWithValue("estado", dto.Estado); camposCambiados.Add("estado"); }
    if (dto.Nivel is not null) { cambios.Add("nivel = @nivel"); cmd.Parameters.AddWithValue("nivel", dto.Nivel); camposCambiados.Add("nivel"); }
    if (dto.Precio is not null) { cambios.Add("precio = @precio"); cmd.Parameters.AddWithValue("precio", dto.Precio.Value); camposCambiados.Add("precio"); }
    if (dto.Minutos is not null) { cambios.Add("minutos = @minutos"); cmd.Parameters.AddWithValue("minutos", dto.Minutos.Value); camposCambiados.Add("minutos"); }
    if (dto.AdelantoPagado is not null) { cambios.Add("adelanto_pagado = @adelanto"); cmd.Parameters.AddWithValue("adelanto", dto.AdelantoPagado.Value); camposCambiados.Add("adelanto"); }
    if (dto.SaldoPagado is not null) { cambios.Add("saldo_pagado = @saldo"); cmd.Parameters.AddWithValue("saldo", dto.SaldoPagado.Value); camposCambiados.Add("saldo"); }

    var hayNota = !string.IsNullOrWhiteSpace(dto.Nota);
    if (cambios.Count == 0 && !hayNota) return Results.BadRequest(new { errores = new[] { "No hay cambios que guardar." } });

    cambios.Add("actualizado_en = now()");
    cmd.CommandText = $"UPDATE solicitudes SET {string.Join(", ", cambios)} WHERE id = @id;";
    cmd.Parameters.AddWithValue("id", id);

    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    string? estadoAnterior;
    await using (var cmdEstado = new NpgsqlCommand("SELECT estado FROM solicitudes WHERE id = @id FOR UPDATE;", conn, tx))
    {
        cmdEstado.Parameters.AddWithValue("id", id);
        estadoAnterior = await cmdEstado.ExecuteScalarAsync() as string;
    }
    if (estadoAnterior is null) return Results.NotFound();

    // Control de concurrencia: si otra persona cambió el estado mientras esta lo miraba, no se pisa ese cambio
    if (dto.EstadoEsperado is not null && dto.EstadoEsperado != estadoAnterior)
        return Results.Json(new { errores = new[] { "El caso cambió mientras lo miraba. Pulse Actualizar y vuelva a intentarlo." } }, statusCode: 409);

    cmd.Connection = conn;
    cmd.Transaction = tx;
    await cmd.ExecuteNonQueryAsync();

    var estadoNuevo = dto.Estado ?? estadoAnterior;
    if (estadoNuevo != estadoAnterior || hayNota)
    {
        await using var cmdHistorial = new NpgsqlCommand(
            "INSERT INTO historial (solicitud_id, estado, nota) VALUES (@id, @estado, @nota);", conn, tx);
        cmdHistorial.Parameters.AddWithValue("id", id);
        cmdHistorial.Parameters.AddWithValue("estado", estadoNuevo);
        cmdHistorial.Parameters.AddWithValue("nota", hayNota ? dto.Nota!.Trim() : DBNull.Value);
        await cmdHistorial.ExecuteNonQueryAsync();
    }

    // Auditoría en la misma transacción: si no se puede registrar, el cambio no se guarda
    if (hayNota) camposCambiados.Add("nota");
    try
    {
        await Auditoria.EscribirEnTx(conn, tx, Ip.Texto(http.Connection.RemoteIpAddress), "actualizar_caso", id, string.Join(",", camposCambiados));
    }
    catch (Exception ex)
    {
        Console.WriteLine("Cambio no guardado por fallo de auditoría: " + ex.Message);
        return Results.StatusCode(503);
    }

    await tx.CommitAsync();
    return Results.NoContent();
});

// ===== Panel: agregar una nota interna (solo la gestora la ve) =====
app.MapPost("/api/casos/{id:guid}/notas", async (Guid id, NotaDto dto, HttpContext http) =>
{
    var texto = (dto.Texto ?? "").Trim();
    if (texto.Length == 0) return Results.BadRequest(new { errores = new[] { "La nota está vacía." } });
    if (texto.Length > 1000) return Results.BadRequest(new { errores = new[] { "La nota es demasiado larga." } });

    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    int filas;
    await using (var cmd = new NpgsqlCommand(
        "INSERT INTO notas (solicitud_id, texto) SELECT id, @texto FROM solicitudes WHERE id = @id;", conn, tx))
    {
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("texto", texto);
        filas = await cmd.ExecuteNonQueryAsync();
    }
    if (filas == 0) return Results.NotFound();

    try
    {
        await Auditoria.EscribirEnTx(conn, tx, Ip.Texto(http.Connection.RemoteIpAddress), "agregar_nota", id, null);
    }
    catch (Exception ex)
    {
        Console.WriteLine("Nota no guardada por fallo de auditoría: " + ex.Message);
        return Results.StatusCode(503);
    }

    await tx.CommitAsync();
    return Results.Json(new { ok = true }, statusCode: 201);
});

app.Run();

// ===== Funciones de apoyo =====

// Respuesta 429 uniforme: mismo formato y cabecera Retry-After en todas las rutas con límite
static IResult Limitado(HttpContext http, string mensaje, TimeSpan reintento)
{
    http.Response.Headers["Retry-After"] = ((int)reintento.TotalSeconds).ToString();
    return Results.Json(new { errores = new[] { mensaje } }, statusCode: 429);
}

// Crea la solicitud, su historial y sus adjuntos en una sola transacción. La auditoría va después y no bloquea.
static async Task<Guid> GuardarSolicitud(string cs, SolicitudDto dto, string codigo,
    List<(string Nombre, string Tipo, byte[] Datos)> adjuntos, string ip)
{
    await using var conn = new NpgsqlConnection(cs);
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
        cmd.Parameters.AddWithValue("telefono", NormalizarTelefono(dto.Telefono));
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

    // La auditoría de altas es de trazabilidad: si falla, la solicitud ya está guardada y no se pierde
    await Auditoria.Segura(cs, ip, "crear_solicitud", id, $"adjuntos={adjuntos.Count}");
    return id;
}

// Código de seguimiento: 8 caracteres sin los que se confunden (0/O, 1/I/L)
static string NuevoCodigo()
{
    const string alfabeto = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    var chars = new char[8];
    for (var i = 0; i < chars.Length; i++)
        chars[i] = alfabeto[RandomNumberGenerator.GetInt32(alfabeto.Length)];
    return new string(chars);
}

static string FormatearCodigo(string codigo) => $"{codigo[..4]}-{codigo[4..]}";

// Solo dígitos ASCII (0-9): char.IsDigit acepta otros alfabetos
static string SoloDigitos(string texto) => new string(texto.Where(c => c >= '0' && c <= '9').ToArray());

// Acepta 10 dígitos o el mismo número con el código de país 57 delante
static string NormalizarTelefono(string texto)
{
    var digitos = SoloDigitos(texto);
    if (digitos.Length == 12 && digitos.StartsWith("57", StringComparison.Ordinal)) digitos = digitos[2..];
    return digitos;
}

// ===== Validación de la solicitud pública =====
static List<string> Validar(SolicitudDto dto)
{
    var errores = new List<string>();
    if (string.IsNullOrWhiteSpace(dto.Nombre)) errores.Add("Falta el nombre.");
    if (dto.Nombre?.Length > 120) errores.Add("El nombre es demasiado largo.");

    if (NormalizarTelefono(dto.Telefono ?? "").Length != 10) errores.Add("El WhatsApp debe tener 10 dígitos.");

    if (string.IsNullOrWhiteSpace(dto.Municipio)) errores.Add("Falta el municipio.");
    if (string.IsNullOrWhiteSpace(dto.Eps)) errores.Add("Falta la EPS.");
    if (string.IsNullOrWhiteSpace(dto.Tramite)) errores.Add("Falta el tipo de trámite.");
    else if (!new[] { "Autorización de medicamento", "Reclamación de medicamento", "PQR", "Otro trámite" }.Contains(dto.Tramite.Trim()))
        errores.Add("El tipo de trámite no es válido.");
    if (dto.Detalle?.Length > 1000) errores.Add("El detalle es demasiado largo.");

    if (dto.Municipio?.Length > 100 || dto.Eps?.Length > 100 || dto.Tramite?.Length > 100)
        errores.Add("Uno de los datos es demasiado largo.");

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

// Nombre con el que se descarga un adjunto: la extensión la decide el servidor según lo que el archivo realmente es
static string NombreDescarga(string nombre, string tipo)
{
    var extension = tipo switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "application/pdf" => ".pdf",
        _ => ".bin"
    };
    var baseNombre = Path.GetFileNameWithoutExtension(nombre);
    foreach (var caracter in Path.GetInvalidFileNameChars()) baseNombre = baseNombre.Replace(caracter, '_');
    if (string.IsNullOrWhiteSpace(baseNombre)) baseNombre = "adjunto";
    if (baseNombre.Length > 80) baseNombre = baseNombre[..80];
    return baseNombre + extension;
}

// Ruta en minúsculas y sin barras repetidas: "//panel" no debe escapar del control de acceso
static string RutaNormalizada(PathString ruta)
{
    var texto = (ruta.Value ?? "").ToLowerInvariant();
    while (texto.Contains("//", StringComparison.Ordinal)) texto = texto.Replace("//", "/", StringComparison.Ordinal);
    return texto;
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

// ===== Tipos de datos =====

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
    int? Precio,
    bool? AdelantoPagado,
    bool? SaldoPagado,
    string? Nota,
    string? EstadoEsperado);

// Nota interna que solo ve la gestora
public record NotaDto(string? Texto);

// ===== Límites de uso en memoria =====
// Un único cerrojo para todas las operaciones: comprobar y registrar es un solo paso, sin carreras.
static class Limitador
{
    private static readonly object cerrojo = new();
    private static readonly Dictionary<string, List<(DateTime Momento, int Peso)>> registros = new();
    // Mayor que cualquier ventana usada: la purga nunca borra intentos que aún cuentan
    private static readonly TimeSpan RetencionMaxima = TimeSpan.FromHours(1);

    // Reserva capacidad si cabe en el máximo dentro de la ventana. Devuelve false si no cabe.
    public static bool Permitir(string clave, int maximo, TimeSpan ventana, int peso = 1)
    {
        var ahora = DateTime.UtcNow;
        lock (cerrojo)
        {
            var lista = Obtener(clave);
            lista.RemoveAll(e => ahora - e.Momento > ventana);
            if (lista.Sum(e => e.Peso) + peso > maximo) return false;
            lista.Add((ahora, peso));
            return true;
        }
    }

    // Solo consulta: no crea entradas ni registra nada
    public static bool Bloqueado(string clave, int maximo, TimeSpan ventana)
    {
        var ahora = DateTime.UtcNow;
        lock (cerrojo)
        {
            if (!registros.TryGetValue(clave, out var lista)) return false;
            lista.RemoveAll(e => ahora - e.Momento > ventana);
            return lista.Sum(e => e.Peso) >= maximo;
        }
    }

    // Registra un fallo sin comprobar nada
    public static void Fallar(string clave)
    {
        lock (cerrojo) Obtener(clave).Add((DateTime.UtcNow, 1));
    }

    // Quita registros más viejos que la retención máxima y las claves que quedan vacías
    public static void Purgar()
    {
        var limite = DateTime.UtcNow - RetencionMaxima;
        lock (cerrojo)
        {
            foreach (var clave in registros.Keys.ToList())
            {
                var lista = registros[clave];
                lista.RemoveAll(e => e.Momento < limite);
                if (lista.Count == 0) registros.Remove(clave);
            }
        }
    }

    private static List<(DateTime Momento, int Peso)> Obtener(string clave)
    {
        if (!registros.TryGetValue(clave, out var lista))
        {
            lista = new List<(DateTime Momento, int Peso)>();
            registros[clave] = lista;
        }
        return lista;
    }
}

// ===== Tareas de mantenimiento periódicas =====
static class Mantenimiento
{
    private static Timer? limpiezaMemoria;
    private static Timer? limpiezaAuditoria;

    public static void Iniciar(string cs, int dias)
    {
        limpiezaMemoria = new Timer(_ => Limitador.Purgar(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
        limpiezaAuditoria = new Timer(async _ =>
        {
            try { await Auditoria.PurgarAntiguos(cs, dias); }
            catch (Exception ex) { Console.WriteLine("Purga de auditoría fallida: " + ex.Message); }
        }, null, TimeSpan.FromHours(24), TimeSpan.FromHours(24));
    }
}

// ===== Auditoría: quién hizo qué, sin datos personales ni contenido =====
static class Auditoria
{
    // Escribe una fila abriendo su propia conexión. Si falla, lanza la excepción.
    public static async Task Escribir(string cs, string ip, string accion, Guid? caso, string? detalle)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await EscribirEnTx(conn, null, ip, accion, caso, detalle);
    }

    // Escribe dentro de una transacción existente: si esta se revierte, la fila también
    public static async Task EscribirEnTx(NpgsqlConnection conn, NpgsqlTransaction? tx, string ip, string accion, Guid? caso, string? detalle)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO auditoria (ip, accion, caso_id, detalle) VALUES (@ip, @accion, @caso, @detalle);", conn, tx);
        cmd.Parameters.AddWithValue("ip", ip);
        cmd.Parameters.AddWithValue("accion", accion);
        cmd.Parameters.AddWithValue("caso", (object?)caso ?? DBNull.Value);
        cmd.Parameters.AddWithValue("detalle", (object?)detalle ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    // Para eventos de acceso fallido: si no se puede escribir, queda en consola y la respuesta sigue
    public static async Task Segura(string cs, string ip, string accion, Guid? caso, string? detalle)
    {
        try { await Escribir(cs, ip, accion, caso, detalle); }
        catch (Exception ex) { Console.WriteLine("Auditoría no guardada (" + accion + "): " + ex.Message); }
    }

    // Para lecturas de datos: si no se puede registrar, el dato no se entrega
    public static async Task<bool> Exigir(string cs, string ip, string accion, Guid? caso, string? detalle)
    {
        try { await Escribir(cs, ip, accion, caso, detalle); return true; }
        catch (Exception ex) { Console.WriteLine("Auditoría fallida (" + accion + "): " + ex.Message); return false; }
    }

    // Borra las filas más antiguas que la retención
    public static async Task PurgarAntiguos(string cs, int dias)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM auditoria WHERE creado_en < now() - make_interval(days => @dias);", conn);
        cmd.Parameters.AddWithValue("dias", dias);
        await cmd.ExecuteNonQueryAsync();
    }
}

// Estado de una clave de envío: nueva, en curso (otro intento la está guardando) o ya registrada
enum EstadoEnvio { Nuevo, EnCurso, Registrado }

// Envíos recientes por clave de idempotencia. Viven en memoria durante una hora. Mientras un intento con la misma
// clave está guardando, los demás reciben "en curso": así un reintento no crea una segunda solicitud.
static class Envios
{
    private static readonly object cerrojo = new();
    private static readonly Dictionary<string, (string Codigo, int Adjuntos, DateTime Momento)> registrados = new();
    private static readonly Dictionary<string, DateTime> enCurso = new();

    // Entre 8 y 64 letras, números o guiones
    public static bool ClaveValida(string clave) =>
        clave.Length >= 8 && clave.Length <= 64 && clave.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    // Solo consulta: no reserva nada
    public static EstadoEnvio Consultar(string clave, out string codigo, out int adjuntos)
    {
        lock (cerrojo)
        {
            Purgar();
            if (registrados.TryGetValue(clave, out var registro))
            {
                codigo = registro.Codigo;
                adjuntos = registro.Adjuntos;
                return EstadoEnvio.Registrado;
            }
            codigo = "";
            adjuntos = 0;
            return enCurso.ContainsKey(clave) ? EstadoEnvio.EnCurso : EstadoEnvio.Nuevo;
        }
    }

    // Reserva la clave para guardar. Si ya estaba registrada o en curso, no la reserva.
    public static EstadoEnvio Reservar(string clave, out string codigo, out int adjuntos)
    {
        lock (cerrojo)
        {
            Purgar();
            if (registrados.TryGetValue(clave, out var registro))
            {
                codigo = registro.Codigo;
                adjuntos = registro.Adjuntos;
                return EstadoEnvio.Registrado;
            }
            codigo = "";
            adjuntos = 0;
            if (enCurso.ContainsKey(clave)) return EstadoEnvio.EnCurso;
            enCurso[clave] = DateTime.UtcNow;
            return EstadoEnvio.Nuevo;
        }
    }

    public static void Confirmar(string clave, string codigo, int adjuntos)
    {
        lock (cerrojo)
        {
            enCurso.Remove(clave);
            registrados[clave] = (codigo, adjuntos, DateTime.UtcNow);
        }
    }

    public static void Liberar(string clave)
    {
        lock (cerrojo) enCurso.Remove(clave);
    }

    // Quita los envíos de más de una hora y las reservas que llevan más de cinco minutos sin terminar
    private static void Purgar()
    {
        var ahora = DateTime.UtcNow;
        foreach (var clave in registrados.Where(p => ahora - p.Value.Momento > TimeSpan.FromHours(1)).Select(p => p.Key).ToList())
            registrados.Remove(clave);
        foreach (var clave in enCurso.Where(p => ahora - p.Value > TimeSpan.FromMinutes(5)).Select(p => p.Key).ToList())
            enCurso.Remove(clave);
    }
}

// ===== Direcciones IP =====
static class Ip
{
    public static string Texto(IPAddress? ip) => ip?.ToString() ?? "desconocida";

    // Agrupa IPv6 por bloque /64: un mismo hogar cambia de dirección dentro de su bloque
    public static string Clave(IPAddress? ip)
    {
        if (ip is null) return "desconocida";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            for (var i = 8; i < 16; i++) bytes[i] = 0;
            return new IPAddress(bytes).ToString() + "/64";
        }
        return ip.ToString();
    }
}
