using Npgsql;

// ===== Configuración =====
// La cadena de conexión viene de appsettings.json o de la variable de entorno
// ConnectionStrings__Postgres. No escriba la contraseña en el código.
var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Falta la cadena de conexión 'Postgres'.");

var app = builder.Build();

// ===== Base de datos: crea la tabla si no existe =====
await using (var conn = new NpgsqlConnection(connectionString))
{
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(@"
        CREATE TABLE IF NOT EXISTS solicitudes (
            id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            nombre                text        NOT NULL,
            telefono              text        NOT NULL,
            municipio             text        NOT NULL,
            eps                   text        NOT NULL,
            tramite               text        NOT NULL,
            detalle               text,
            consentimiento        boolean     NOT NULL,
            estado                text        NOT NULL DEFAULT 'Recibido',
            creado_en             timestamptz NOT NULL DEFAULT now()
        );", conn);
    await cmd.ExecuteNonQueryAsync();
}

// ===== Página estática (wwwroot/index.html) =====
app.UseDefaultFiles();
app.UseStaticFiles();

// ===== API: guardar una solicitud =====
app.MapPost("/api/solicitudes", async (SolicitudDto dto) =>
{
    var errores = Validar(dto);
    if (errores.Count > 0)
        return Results.BadRequest(new { errores });

    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(@"
        INSERT INTO solicitudes (nombre, telefono, municipio, eps, tramite, detalle, consentimiento)
        VALUES (@nombre, @telefono, @municipio, @eps, @tramite, @detalle, @consentimiento)
        RETURNING id;", conn);

    cmd.Parameters.AddWithValue("nombre", dto.Nombre.Trim());
    cmd.Parameters.AddWithValue("telefono", new string(dto.Telefono.Where(char.IsDigit).ToArray()));
    cmd.Parameters.AddWithValue("municipio", dto.Municipio.Trim());
    cmd.Parameters.AddWithValue("eps", dto.Eps.Trim());
    cmd.Parameters.AddWithValue("tramite", dto.Tramite.Trim());
    cmd.Parameters.AddWithValue("detalle", string.IsNullOrWhiteSpace(dto.Detalle) ? DBNull.Value : dto.Detalle.Trim());
    cmd.Parameters.AddWithValue("consentimiento", dto.Consentimiento);

    var id = await cmd.ExecuteScalarAsync();
    return Results.Created($"/api/solicitudes/{id}", new { id });
});

app.Run();

// ===== Validación de los datos recibidos =====
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

// Datos que llegan del formulario
public record SolicitudDto(
    string Nombre,
    string Telefono,
    string Municipio,
    string Eps,
    string Tramite,
    string? Detalle,
    bool Consentimiento);
