// Terazi tuş kağıdı sunucusu: sayfayı sunar, düzenleri (market tasarımlarını) isimleriyle SQLite'ta tutar.
// Okumak serbest; kaydetmek/silmek için X-Pin başlığında TERAZI_PIN ortam değişkenindeki şifre gerekir.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

var dataDir = Environment.GetEnvironmentVariable("TERAZI_DATA") ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);
var connStr = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDir, "terazi.db") }.ToString();
var pin = Environment.GetEnvironmentVariable("TERAZI_PIN") ?? "";
const int MaxBody = 512 * 1024;

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    // GitHub Pages kopyası ve bilgisayarda dosyadan açılan sayfa (origin "null") da kullanabilsin
    .WithOrigins("https://mhertan.github.io", "null")
    .WithHeaders("Content-Type", "X-Pin")
    .WithMethods("GET", "PUT", "DELETE", "POST")));

// Şifre denemesini yavaşlatmak için yazma uçlarında IP başına dakikada 20 istek
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("yazma", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

using (var c = Open())
{
    Exec(c, "PRAGMA journal_mode=WAL");
    Exec(c, "CREATE TABLE IF NOT EXISTS duzenler (ad TEXT PRIMARY KEY, veri TEXT NOT NULL, guncelleme TEXT NOT NULL)");
    SeedIfEmpty(c);
}

// Coolify/Traefik arkasında gerçek istemci IP'si (hız sınırı için)
var fwd = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
fwd.KnownIPNetworks.Clear();
fwd.KnownProxies.Clear();
app.UseForwardedHeaders(fwd);

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // Sayfa güncellenince hemen gelsin
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
});
app.UseCors();
app.UseRateLimiter();

app.MapGet("/saglik", () => "ok");
// Eski adres: tarayıcı yönlendirmede linkin #d=... kısmını korur
app.MapGet("/tus-kagidi.html", () => Results.Redirect("/"));

app.MapGet("/api/duzenler", () =>
{
    using var c = Open();
    var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT ad, guncelleme FROM duzenler ORDER BY ad COLLATE NOCASE";
    using var r = cmd.ExecuteReader();
    var list = new List<object>();
    while (r.Read()) list.Add(new { ad = r.GetString(0), guncelleme = r.GetString(1) });
    return Results.Ok(list);
});

app.MapGet("/api/duzenler/{ad}", (string ad) =>
{
    using var c = Open();
    var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT veri FROM duzenler WHERE ad = $ad";
    cmd.Parameters.AddWithValue("$ad", ad.Trim());
    return cmd.ExecuteScalar() is string veri ? Results.Content(veri, "application/json; charset=utf-8") : Results.NotFound();
});

app.MapPost("/api/pin", (HttpRequest req) => PinCheck(req) ?? Results.NoContent())
    .RequireRateLimiting("yazma");

app.MapPut("/api/duzenler/{ad}", async (string ad, HttpRequest req) =>
{
    if (PinCheck(req) is { } fail) return fail;
    ad = ad.Trim();
    if (!ValidName(ad)) return Results.BadRequest("Düzen adı 1-60 karakter olmalı.");
    if (req.ContentLength is > MaxBody) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

    using var reader = new StreamReader(req.Body, Encoding.UTF8);
    var body = await reader.ReadToEndAsync();
    if (body.Length > MaxBody) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    try
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return Results.BadRequest("Geçersiz veri.");
    }
    catch (JsonException)
    {
        return Results.BadRequest("Geçersiz veri.");
    }

    using var c = Open();
    var cmd = c.CreateCommand();
    cmd.CommandText = """
        INSERT INTO duzenler (ad, veri, guncelleme) VALUES ($ad, $veri, $t)
        ON CONFLICT(ad) DO UPDATE SET veri = excluded.veri, guncelleme = excluded.guncelleme
        """;
    cmd.Parameters.AddWithValue("$ad", ad);
    cmd.Parameters.AddWithValue("$veri", body);
    cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
    cmd.ExecuteNonQuery();
    return Results.NoContent();
}).RequireRateLimiting("yazma");

app.MapDelete("/api/duzenler/{ad}", (string ad, HttpRequest req) =>
{
    if (PinCheck(req) is { } fail) return fail;
    using var c = Open();
    var cmd = c.CreateCommand();
    cmd.CommandText = "DELETE FROM duzenler WHERE ad = $ad";
    cmd.Parameters.AddWithValue("$ad", ad.Trim());
    return cmd.ExecuteNonQuery() > 0 ? Results.NoContent() : Results.NotFound();
}).RequireRateLimiting("yazma");

app.Run();

SqliteConnection Open()
{
    var c = new SqliteConnection(connStr);
    c.Open();
    return c;
}

static void Exec(SqliteConnection c, string sql)
{
    var cmd = c.CreateCommand();
    cmd.CommandText = sql;
    cmd.ExecuteNonQuery();
}

static bool ValidName(string ad) => ad.Length is > 0 and <= 60 && !ad.Any(char.IsControl);

// null: şifre doğru; aksi halde döndürülecek hata
IResult? PinCheck(HttpRequest req)
{
    if (pin.Length == 0) return Results.Problem("Sunucuda TERAZI_PIN ayarlı değil.", statusCode: 503);
    var given = Encoding.UTF8.GetBytes(req.Headers["X-Pin"].ToString());
    return CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(pin)) ? null : Results.Unauthorized();
}

// İlk açılışta seed/ klasöründeki düzenleri yükle (veritabanı boşsa)
void SeedIfEmpty(SqliteConnection c)
{
    var count = c.CreateCommand();
    count.CommandText = "SELECT COUNT(*) FROM duzenler";
    if ((long)count.ExecuteScalar()! > 0) return;
    var seedDir = Path.Combine(AppContext.BaseDirectory, "seed");
    if (!Directory.Exists(seedDir)) return;
    foreach (var file in Directory.GetFiles(seedDir, "*.json"))
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO duzenler (ad, veri, guncelleme) VALUES ($ad, $veri, $t)";
        cmd.Parameters.AddWithValue("$ad", Path.GetFileNameWithoutExtension(file));
        cmd.Parameters.AddWithValue("$veri", File.ReadAllText(file, Encoding.UTF8));
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }
}
