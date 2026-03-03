using System.Data;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using QRCoder;
using Serilog;
using Serilog.Events;
using TimeAttendance.Api.Infrastructure;
using TimeAttendance.Api.Models;

var builder = WebApplication.CreateBuilder(args);

// ===== SERILOG (API) =====
// Logs are written to: %AppData%\ChamCong\logs\api-YYYY-MM-DD.log
var logDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "ChamCong",
    "logs");
Directory.CreateDirectory(logDir);

builder.Host.UseSerilog((ctx, lc) =>
{
    lc.MinimumLevel.Information()
      .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
      .MinimumLevel.Override("System", LogEventLevel.Warning)
      .Enrich.FromLogContext()
      .WriteTo.File(
          path: Path.Combine(logDir, "api-.log"),
          rollingInterval: RollingInterval.Day,
          retainedFileCountLimit: 14,
          fileSizeLimitBytes: 10_000_000,
          rollOnFileSizeLimit: true,
          shared: true,
          flushToDiskInterval: TimeSpan.FromSeconds(1));
});

// Optional allow-list (comma-separated) for kiosk PCs that should bypass device binding (fallback).
// Example: DeviceBinding:BypassIps: "192.168.1.10,192.168.1.11"
var bypassIpList = (builder.Configuration["DeviceBinding:BypassIps"] ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(s => IPAddress.TryParse(s, out var ip) ? ip : null)
    .Where(ip => ip != null)
    .Cast<IPAddress>()
    .ToHashSet();

// Decide whether to bypass device binding.
// Priority:
// 1) Kiosk secret header (recommended, IP-independent)
// 2) Fallback IP bypass (loopback or allow-list)
static bool IsKioskBypass(HttpContext ctx, IConfiguration cfg, HashSet<IPAddress> bypassIpList)
{
    try
    {
        // 1) Secret header (recommended)
        var headerName = cfg["Kiosk:HeaderName"] ?? "X-Kiosk-Secret";
        var secret = cfg["Kiosk:Secret"];
        if (!string.IsNullOrWhiteSpace(secret))
        {
            var incoming = ctx.Request.Headers[headerName].ToString();
            if (!string.IsNullOrWhiteSpace(incoming) && incoming == secret)
                return true;
        }

        // 2) Fallback: loopback / allow-list IPs
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip == null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        return IPAddress.IsLoopback(ip) || bypassIpList.Contains(ip);
    }
    catch
    {
        return false;
    }
}

// Bind URLs (default: listen on all interfaces)
var urls = builder.Configuration["Server:Urls"] ?? "http://0.0.0.0:5000";
builder.WebHost.UseUrls(urls);

Log.Information("API bind urls: {Urls}", urls);

// Services
builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
builder.Services.AddSingleton<IKioskStore, KioskStore>();
builder.Services.AddSingleton<IDeviceBindingStore, DeviceBindingStore>();

var app = builder.Build();

// Request logging: method/path/status/elapsed
app.UseSerilogRequestLogging();
app.UseStaticFiles();

// Create kiosk row (DeviceCode + SharedSecret) if missing
var kioskCode = app.Configuration["Kiosk:DeviceCode"] ?? "KIOSK1";
var kioskName = app.Configuration["Kiosk:DeviceName"] ?? "Quay 1";
await app.Services.GetRequiredService<IKioskStore>().GetOrCreateAsync(kioskCode, kioskName);

app.MapGet("/", () => Results.Redirect("/kiosk/index.html"));
app.MapGet("/health", () => Results.Ok(new { ok = true, time = DateTimeOffset.Now }));


// -------- KIOSK QR --------

app.MapGet("/kiosk/token", async (HttpContext http, IKioskStore kiosks) =>
{
    var kiosk = await kiosks.GetOrCreateAsync(kioskCode, kioskName);
    var stepSeconds = 30;
    var counter = TokenSigner.CurrentCounter(stepSeconds);
    var signature = TokenSigner.Sign(kiosk.SharedSecret, kiosk.DeviceCode, counter);

    // URL đưa vào QR: ưu tiên PublicBaseUrl (khi deploy public), nếu không thì lấy theo request hiện tại
    var baseUrl = app.Configuration["Server:PublicBaseUrl"];
    if (string.IsNullOrWhiteSpace(baseUrl))
        baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
    baseUrl = baseUrl.TrimEnd('/');

    // New mobile page (QR + PIN) - white theme UI
    var url = $"{baseUrl}/m/attendance_qr.html?k={Uri.EscapeDataString(kiosk.DeviceCode)}&c={counter}&s={Uri.EscapeDataString(signature)}";

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var expiresIn = stepSeconds - (int)(now % stepSeconds);

    return Results.Ok(new TokenDto(kiosk.DeviceCode, counter, signature, url, expiresIn));
});


app.MapGet("/kiosk/qr.png", async (HttpContext http, IKioskStore kiosks) =>
{
    var kiosk = await kiosks.GetOrCreateAsync(kioskCode, kioskName);
    var stepSeconds = 30;
    var counter = TokenSigner.CurrentCounter(stepSeconds);
    var signature = TokenSigner.Sign(kiosk.SharedSecret, kiosk.DeviceCode, counter);

    var baseUrl = app.Configuration["Server:PublicBaseUrl"];
    if (string.IsNullOrWhiteSpace(baseUrl))
        baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
    baseUrl = baseUrl.TrimEnd('/');

    var url = $"{baseUrl}/m/attendance_qr.html?k={Uri.EscapeDataString(kiosk.DeviceCode)}&c={counter}&s={Uri.EscapeDataString(signature)}";

    using var gen = new QRCodeGenerator();
    using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
    using var png = new PngByteQRCode(data);
    var bytes = png.GetGraphic(12);

    http.Response.Headers.CacheControl = "no-store";
    return Results.File(bytes, "image/png");
});


// -------- MOBILE API --------

app.MapGet("/api/employees/active", async (ISqlConnectionFactory db) =>
{
    using var conn = db.Create();
    var list = await conn.QueryAsync<EmployeeBriefDto>(
        "SELECT EmployeeCode, FullName FROM dbo.Employees WHERE IsActive=1 ORDER BY FullName");
    return Results.Ok(new ApiResponse<IEnumerable<EmployeeBriefDto>>(true, "OK", list));
});


app.MapPost("/api/attendance/checkin", async (HttpContext ctx, AttendanceActionRequest req, IKioskStore kiosks, IDeviceBindingStore devices, ISqlConnectionFactory db) =>
{
    var token = await ValidateTokenAsync(req, kiosks, kioskCode);
    if (!token.Ok) return Results.BadRequest(new ApiResponse<object>(false, token.Message, null, token.ErrorCode));

    // Device binding
    var deviceToken = (req.DeviceToken ?? string.Empty).Trim();
    var deviceLabel = ctx.Request.Headers.UserAgent.ToString();
    if (deviceLabel.Length > 250) deviceLabel = deviceLabel.Substring(0, 250);

    // ✅ Kiosk bypass by secret header (or fallback IP allowlist/loopback)
    var bypassOnKiosk = IsKioskBypass(ctx, app.Configuration, bypassIpList);

    if (!bypassOnKiosk)
    {
        var dev = await devices.EnsureApprovedAsync(req.EmployeeCode, deviceToken, deviceLabel, req.ManagerCode, ctx.RequestAborted);
        if (!dev.Ok)
            return Results.Json(new
            {
                requiresManager = true,
                message = "Thiết bị mới. Vui lòng nhập mã quản lý để duyệt."
            }, statusCode: 409);
    }

    try
    {
        using var conn = db.Create();
        var p = new DynamicParameters();
        p.Add("@EmployeeCode", req.EmployeeCode);
        p.Add("@Pin", req.Pin);
        p.Add("@DeviceGuid", token.DeviceGuid);
        p.Add("@EventTime", null);
        p.Add("@OtpCounter", req.Counter);
        p.Add("@ClientIp", ctx.Connection.RemoteIpAddress?.ToString());
        p.Add("@UserAgent", ctx.Request.Headers.UserAgent.ToString());
        p.Add("@Note", $"Mobile|dev={deviceToken}");

        var sp = await conn.QuerySingleAsync<SpApiResult>("att.usp_CheckIn", p, commandType: CommandType.StoredProcedure);

        if (!sp.ok)
        {
            return Results.Json(
                new { ok = false, message = sp.message, data = (object?)null, errorCode = sp.errorCode },
                statusCode: StatusCodes.Status409Conflict
            );
        }

        return Results.Ok(new { ok = true, message = sp.message, data = (object?)null, errorCode = (int?)null });
    }
    catch (SqlException ex)
    {
        var mapped = SqlErrorMapper.Map(ex);
        return Results.BadRequest(new ApiResponse<object>(false, mapped.Message, null, mapped.Code));
    }
});

app.MapPost("/api/attendance/checkout", async (HttpContext ctx, AttendanceActionRequest req, IKioskStore kiosks, IDeviceBindingStore devices, ISqlConnectionFactory db) =>
{
    var token = await ValidateTokenAsync(req, kiosks, kioskCode);
    if (!token.Ok) return Results.BadRequest(new ApiResponse<object>(false, token.Message, null, token.ErrorCode));

    // Device binding
    var deviceToken = (req.DeviceToken ?? string.Empty).Trim();
    var deviceLabel = ctx.Request.Headers.UserAgent.ToString();
    if (deviceLabel.Length > 250) deviceLabel = deviceLabel.Substring(0, 250);

    // ✅ Kiosk bypass by secret header (or fallback IP allowlist/loopback)
    var bypassOnKiosk = IsKioskBypass(ctx, app.Configuration, bypassIpList);

    if (!bypassOnKiosk)
    {
        var dev = await devices.EnsureApprovedAsync(req.EmployeeCode, deviceToken, deviceLabel, req.ManagerCode, ctx.RequestAborted);
        if (!dev.Ok)
            return Results.Json(new
            {
                requiresManager = true,
                message = "Thiết bị mới. Vui lòng nhập mã quản lý để duyệt."
            }, statusCode: 409);
    }

    try
    {
        using var conn = db.Create();
        var p = new DynamicParameters();
        p.Add("@EmployeeCode", req.EmployeeCode);
        p.Add("@Pin", req.Pin);
        p.Add("@DeviceGuid", token.DeviceGuid);
        p.Add("@EventTime", null);
        p.Add("@OtpCounter", req.Counter);
        p.Add("@ClientIp", ctx.Connection.RemoteIpAddress?.ToString());
        p.Add("@UserAgent", ctx.Request.Headers.UserAgent.ToString());
        p.Add("@Note", $"Mobile|dev={deviceToken}");

        var sp = await conn.QuerySingleAsync<SpApiResult>("att.usp_CheckOut", p, commandType: CommandType.StoredProcedure);

        if (!sp.ok)
        {
            return Results.Json(
                new { ok = false, message = sp.message, data = (object?)null, errorCode = sp.errorCode },
                statusCode: StatusCodes.Status409Conflict
            );
        }

        return Results.Ok(new { ok = true, message = sp.message, data = (object?)null, errorCode = (int?)null });
    }
    catch (SqlException ex)
    {
        var mapped = SqlErrorMapper.Map(ex);
        return Results.BadRequest(new ApiResponse<object>(false, mapped.Message, null, mapped.Code));
    }
});

static string Base64Url(byte[] bytes)
{
    return Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}

app.MapGet("/api/kiosk/token", async (string k, HttpRequest req, IConfiguration cfg) =>
{
    if (string.IsNullOrWhiteSpace(k))
        return Results.BadRequest(new { message = "Missing kiosk code (k)" });

    var connStr = cfg.GetConnectionString("Db");
    if (string.IsNullOrWhiteSpace(connStr))
        return Results.Problem("Missing ConnectionStrings:Db");

    // Lấy SharedSecret của kiosk từ DB
    byte[]? secret;
    await using (var cn = new SqlConnection(connStr))
    {
        await cn.OpenAsync();

        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP 1 SharedSecret
            FROM dbo.KioskDevices
            WHERE DeviceCode = @k AND IsActive = 1";
        cmd.Parameters.AddWithValue("@k", k.Trim());

        secret = (byte[]?)await cmd.ExecuteScalarAsync();
    }

    if (secret == null || secret.Length == 0)
        return Results.NotFound(new { message = $"Kiosk '{k}' not found/active" });

    // Counter theo 30s
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var counter = now / 30;
    var expires = 30 - (int)(now % 30);

    // Ký HMAC: payload = "KIOSK1|<counter>"
    var payload = $"{k.Trim()}|{counter}";
    using var hmac = new HMACSHA256(secret);
    var sig = Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));

    var baseUrl = cfg["Server:PublicBaseUrl"];
    if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = $"{req.Scheme}://{req.Host}";
    baseUrl = baseUrl.TrimEnd('/');
    var url = $"{baseUrl}/m/attendance_qr.html?k={Uri.EscapeDataString(k.Trim())}&c={counter}&s={sig}";

    return Results.Ok(new { url, expiresInSeconds = expires });
});

// Flush logs when stopping
app.Lifetime.ApplicationStopped.Register(Log.CloseAndFlush);

app.Run();

// ---------------- helpers ----------------

static async Task<(bool Ok, string Message, Guid DeviceGuid, int? ErrorCode)> ValidateTokenAsync(
    AttendanceActionRequest req,
    IKioskStore kiosks,
    string allowedKioskCode)
{
    if (string.IsNullOrWhiteSpace(req.EmployeeCode)) return (false, "Thiếu mã nhân viên", Guid.Empty, 400);
    if (string.IsNullOrWhiteSpace(req.Pin)) return (false, "Thiếu PIN", Guid.Empty, 400);
    if (string.IsNullOrWhiteSpace(req.KioskCode) || req.Counter <= 0 || string.IsNullOrWhiteSpace(req.Signature))
        return (false, "QR không hợp lệ (thiếu tham số)", Guid.Empty, 400);

    if (!string.Equals(req.KioskCode, allowedKioskCode, StringComparison.OrdinalIgnoreCase))
        return (false, "Kiosk không hợp lệ", Guid.Empty, 401);

    var kiosk = await kiosks.GetOrCreateAsync(allowedKioskCode, allowedKioskCode);

    var nowCounter = TokenSigner.CurrentCounter(30);
    if (!TokenSigner.IsCounterWithinWindow(req.Counter, nowCounter, window: 1))
        return (false, "QR đã hết hạn. Vui lòng quét lại.", Guid.Empty, 401);

    if (!TokenSigner.Verify(kiosk.SharedSecret, kiosk.DeviceCode, req.Counter, req.Signature))
        return (false, "QR không hợp lệ.", Guid.Empty, 401);

    return (true, "OK", kiosk.DeviceGuid, null);
}
