using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Forms;
using TimeAttendance.WinForms.Infrastructure;
using TimeAttendance.WinForms.Core;
using Azure.Core;
using System.Threading;
using System.Security.Cryptography;
using System.Text;
using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;


namespace TimeAttendance.WinForms
{
    public partial class MainForm : Form
    {

        private readonly ILogger<MainForm> _logger;
        private readonly IConfiguration _config;
        private readonly IAttendanceRepository _attendance;
        private readonly IDashboardRepository _dashboard;
        private readonly IKioskApiClient _apiClient;
        private readonly IEmployeeRepository _employees;
        private readonly IPayrollRepository _payroll;
        private readonly IAdminPinRepository _adminPin;
        private readonly Services.IPayrollExcelExporter _payrollExcel;
        private readonly Services.IScheduleWeekExcelExporter _scheduleWeekExcel;
        private readonly IKioskRepository _kioskRepo;
        private readonly string _connectionString;
        private readonly ShiftRepository _shiftRepo;
        private readonly ScheduleRepository _scheduleRepo;
        private readonly PayMultiplierRepository _payMultiplierRepo;

        private WebView2 _wv = new();

        private bool _initialized;

        private static string GetLocalConfigPath()
        {
            // User-editable config (no rebuild): %AppData%\ChamCong\appsettings.local.json
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChamCong");
            return Path.Combine(dir, "appsettings.local.json");
        }

        private static bool TryParseEndpoint(string? baseUrl, out string scheme, out string host, out int port)
        {
            scheme = "http";
            host = "localhost";
            port = 5000;

            if (string.IsNullOrWhiteSpace(baseUrl)) return false;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return false;

            scheme = uri.Scheme;
            host = uri.Host;
            port = uri.Port;
            return true;
        }

        private static string ReplaceUrlBase(string fullUrl, string newBase)
        {
            if (!Uri.TryCreate(fullUrl, UriKind.Absolute, out var oldUri)) return fullUrl;
            if (!Uri.TryCreate(newBase, UriKind.Absolute, out var baseUri)) return fullUrl;

            var b = new UriBuilder(oldUri)
            {
                Scheme = baseUri.Scheme,
                Host = baseUri.Host,
                Port = baseUri.Port
            };
            return b.Uri.ToString();
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        private string ResolveExportDir()
        {
            var configured = (_config["Export:OutputDir"] ?? "").Trim();
            if (!string.IsNullOrEmpty(configured)) return configured;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ChamCong", "Exports");
        }
        private Task<string?> PromptSaveExcelPathAsync(string defaultFileName)
        {
            var tcs = new TaskCompletionSource<string?>();

            void Show()
            {
                try
                {
                    using var sfd = new SaveFileDialog
                    {
                        Title = "Chọn nơi lưu file Excel",
                        Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                        FileName = defaultFileName,
                        AddExtension = true,
                        DefaultExt = "xlsx",
                        OverwritePrompt = true
                    };

                    var dr = sfd.ShowDialog(this);
                    tcs.SetResult(dr == DialogResult.OK ? sfd.FileName : null);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }

            if (InvokeRequired) BeginInvoke((Action)Show);
            else Show();

            return tcs.Task;
        }

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        public MainForm(
            ILogger<MainForm> logger,
            IConfiguration config,
            IAttendanceRepository attendance,
            IDashboardRepository dashboard,
            IKioskRepository kioskRepo,
            IKioskApiClient apiClient,
            IEmployeeRepository employees,
            IPayrollRepository payroll,
            IAdminPinRepository adminPin,
            Services.IPayrollExcelExporter payrollExcel,
            Services.IScheduleWeekExcelExporter scheduleWeekExcel
            )
        {
            _logger = logger;
            _apiClient = apiClient;
            _config = config;
            _attendance = attendance;
            _dashboard = dashboard;
            _kioskRepo = kioskRepo;
            _employees = employees;
            _payroll = payroll;
            _adminPin = adminPin;
            _payrollExcel = payrollExcel;
            _scheduleWeekExcel = scheduleWeekExcel;
            _connectionString =
            _config.GetConnectionString("Db")
            ?? throw new InvalidOperationException("Missing ConnectionStrings:Db in appsettings.json");

            _shiftRepo = new ShiftRepository(_connectionString);
            _scheduleRepo = new ScheduleRepository(_connectionString);
            _payMultiplierRepo = new PayMultiplierRepository(_connectionString);


            InitializeComponent();
            WindowState = FormWindowState.Maximized;
            _wv.Dock = DockStyle.Fill;
            Controls.Add(_wv);

            Shown += async (_, __) => await InitWebAsync();
        }

        private async Task InitWebAsync()
        {
            if (_initialized) return;
            _initialized = true;

            // 1) Ensure kiosk record tồn tại trước
            var deviceCode = _config["Kiosk:DeviceCode"] ?? "KIOSK1";
            if (!Guid.TryParse(_config["Kiosk:DeviceGuid"], out var deviceGuid) || deviceGuid == Guid.Empty)
            {
                // fallback: stable guid by machine + deviceCode (so app can run even if config hasn't been set)
                deviceGuid = StableGuid($"{Environment.MachineName}|{deviceCode}");
            }
            var deviceName = _config["Kiosk:DeviceName"] ?? "Quầy 1";
            await _kioskRepo.EnsureKioskAsync(deviceGuid, deviceCode, deviceName);

            // 1b) Ensure Admin PIN initialized (auto-create table + seed default if missing)
            await _adminPin.EnsureInitializedAsync();

            // 2) Init WebView2
            await _wv.EnsureCoreWebView2Async();
            // _wv.CoreWebView2.OpenDevToolsWindow();

            _wv.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            // Map local folder -> https://app.local/
            var webRoot = Path.Combine(AppContext.BaseDirectory, "www");

            if (!Directory.Exists(webRoot) || !File.Exists(Path.Combine(webRoot, "index.html")))
            {
                _initialized = false;
                MessageBox.Show(
                    $"Không tìm thấy UI tại: {webRoot}\\index.html\n\n" +
                    "Hãy đảm bảo thư mục www được CopyToOutputDirectory trong .csproj.",
                    "Missing UI",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            _wv.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "app.local",
                webRoot,
                CoreWebView2HostResourceAccessKind.Allow);
            _wv.CoreWebView2.Navigate("https://app.local/index.html");
            _logger.LogInformation("Web UI loaded from: {WebRoot}", webRoot);
        }

        private static Guid StableGuid(string input)
        {
            var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(input));
            var g = new byte[16];
            Array.Copy(bytes, g, 16);
            return new Guid(g);
        }

        private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            BridgeRequest? req = null;
            try
            {
                req = JsonSerializer.Deserialize<BridgeRequest>(e.WebMessageAsJson, JsonOpts);
                if (req is null) return;

                var resp = await DispatchAsync(req, CancellationToken.None);
                _wv.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(resp, JsonOpts));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WebMessage error");

                // Always respond so the UI doesn't hang.
                try
                {
                    var id = req?.Id ?? "unknown";
                    var resp = BridgeResponse.Fail(id, "Lỗi hệ thống (xem log).", null);
                    _wv.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(resp, JsonOpts));
                }
                catch
                {
                    // ignore
                }
            }
        }

        private sealed class SpApiResult
        {
            public bool ok { get; set; }
            public string? message { get; set; }
            public int? errorCode { get; set; }
        }



        private async Task<BridgeResponse> DispatchAsync(BridgeRequest req, CancellationToken ct)
        {
            var deviceCode = _config["Kiosk:DeviceCode"] ?? "KIOSK1";
            if (!Guid.TryParse(_config["Kiosk:DeviceGuid"], out var deviceGuid) || deviceGuid == Guid.Empty)
            {
                deviceGuid = StableGuid($"{Environment.MachineName}|{deviceCode}");
            }

            try
            {
                switch (req.Type)
                {
                    case "SETTINGS_GET":
                        {
                            // Prefer PublicBaseUrl for QR; fallback to BaseUrl
                            var publicBase = _config.GetValue<string>("Api:PublicBaseUrl");
                            var baseUrl = string.IsNullOrWhiteSpace(publicBase)
                                ? (_config.GetValue<string>("Api:BaseUrl") ?? "http://localhost:5000")
                                : publicBase;

                            TryParseEndpoint(baseUrl, out var scheme, out var host, out var port);

                            return BridgeResponse.Ok(req.Id, "OK", new
                            {
                                api = new { scheme, host, port },
                                localConfigPath = GetLocalConfigPath()
                            });
                        }

                    case "SETTINGS_SAVE":
                        {
                            var p = req.Payload.Deserialize<SettingsSavePayload>(JsonOpts);
                            if (p is null) return BridgeResponse.Fail(req.Id, "Payload không hợp lệ.");

                            var scheme = (p.Api.Scheme ?? "http").Trim().ToLowerInvariant();
                            if (scheme != "http" && scheme != "https") return BridgeResponse.Fail(req.Id, "Scheme chỉ hỗ trợ http/https.");
                            var host = (p.Api.Host ?? "").Trim();
                            if (string.IsNullOrWhiteSpace(host)) return BridgeResponse.Fail(req.Id, "Vui lòng nhập IP/Host.");
                            var port = p.Api.Port;
                            if (port < 1 || port > 65535) return BridgeResponse.Fail(req.Id, "Port không hợp lệ.");

                            var url = $"{scheme}://{host}:{port}";

                            // Build json override file. This file is loaded by Program.cs so no rebuild is needed.
                            var localPath = GetLocalConfigPath();
                            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

                            // Keep existing values if user does not want to overwrite.
                            var currentBase = _config.GetValue<string>("Api:BaseUrl") ?? "http://localhost:5000";
                            var currentPublic = _config.GetValue<string>("Api:PublicBaseUrl") ?? "";
                            var newBase = p.ApplyToBaseUrl ? url : currentBase;
                            var newPublic = p.ApplyToPublicBaseUrl ? url : currentPublic;

                            var obj = new
                            {
                                Api = new
                                {
                                    BaseUrl = newBase,
                                    PublicBaseUrl = newPublic
                                }
                            };
                            File.WriteAllText(localPath, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));

                            return BridgeResponse.Ok(req.Id, $"Đã lưu: {url}");
                        }

                    case "API_TEST":
                        {
                            var p = req.Payload.Deserialize<ApiTestPayload>(JsonOpts);
                            if (p is null) return BridgeResponse.Fail(req.Id, "Payload không hợp lệ.");
                            var scheme = (p.Scheme ?? "http").Trim().ToLowerInvariant();
                            if (scheme != "http" && scheme != "https") return BridgeResponse.Fail(req.Id, "Scheme chỉ hỗ trợ http/https.");
                            var host = (p.Host ?? "").Trim();
                            if (string.IsNullOrWhiteSpace(host)) return BridgeResponse.Fail(req.Id, "Thiếu IP/Host.");
                            if (p.Port < 1 || p.Port > 65535) return BridgeResponse.Fail(req.Id, "Port không hợp lệ.");

                            var baseUrl = $"{scheme}://{host}:{p.Port}";
                            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

                            try
                            {
                                var resp = await http.GetAsync($"{baseUrl.TrimEnd('/')}/health", ct);
                                if (!resp.IsSuccessStatusCode)
                                    return BridgeResponse.Fail(req.Id, $"Không OK: HTTP {(int)resp.StatusCode}.");

                                return BridgeResponse.Ok(req.Id, "Kết nối OK");
                            }
                            catch (Exception ex)
                            {
                                return BridgeResponse.Fail(req.Id, ex.Message);
                            }
                        }

                    case "EMP_LIST":
                        {
                            var p = req.Payload.Deserialize<EmployeeListPayload>(JsonOpts);
                            var includeInactive = p?.IncludeInactive ?? false;
                            var data = await _employees.ListAsync(includeInactive);
                            return BridgeResponse.Ok(req.Id, "OK", data);
                        }
                    /*            case "CHECK_IN":
                                    {
                                        var p = req.Payload.Deserialize<CheckActionPayload>(JsonOpts);
                                        if (p is null) return BridgeResponse.Fail(req.Id, "Payload không hợp lệ.");
                                        await _attendance.CheckInAsync(p.EmployeeCode, p.Pin, deviceGuid);
                                        return BridgeResponse.Ok(req.Id, "Check-in thành công");
                                    }
                                case "CHECK_OUT":
                                    {
                                        var p = req.Payload.Deserialize<CheckActionPayload>(JsonOpts);
                                        if (p is null) return BridgeResponse.Fail(req.Id, "Payload không hợp lệ.");
                                        await _attendance.CheckOutAsync(p.EmployeeCode, p.Pin, deviceGuid);
                                        return BridgeResponse.Ok(req.Id, "Check-out thành công");
                                    }*/

                    case "CHECK_IN":
                        {
                            var p = req.Payload.Deserialize<CheckActionPayload>(JsonOpts);
                            if (p is null) return BridgeResponse.Fail(req.Id, "Payload không hợp lệ.");

                            try
                            {
                                await using var conn = new SqlConnection(_connectionString);
                                await conn.OpenAsync(ct);

                                var dp = new DynamicParameters();
                                dp.Add("@EmployeeCode", p.EmployeeCode);
                                dp.Add("@Pin", p.Pin);
                                dp.Add("@DeviceGuid", deviceGuid);
                                dp.Add("@EventTime", null);
                                dp.Add("@OtpCounter", null);
                                dp.Add("@ClientIp", null);
                                dp.Add("@UserAgent", "WinForms");
                                dp.Add("@Note", "WinForms");

                                var sp = await conn.QuerySingleAsync<SpApiResult>(
                                    "att.usp_CheckIn",
                                    dp,
                                    commandType: CommandType.StoredProcedure
                                );

                                if (sp == null || !sp.ok)
                                {
                                    // ok=false => UI JS sẽ catch và hiện lỗi đúng
                                    return new BridgeResponse(req.Id, false, sp?.message ?? "Check-in thất bại.", null, sp?.errorCode);
                                }

                                return new BridgeResponse(req.Id, true, sp.message ?? "Check-in thành công.", null, null);
                            }
                            catch (SqlException ex)
                            {
                                // Nếu bạn muốn map lỗi đẹp như API thì làm mapper sau,
                                // trước mắt trả message để UI thấy đúng lỗi SQL
                                return new BridgeResponse(req.Id, false, ex.Message, null, ex.Number);
                            }
                            catch (Exception ex)
                            {
                                return new BridgeResponse(req.Id, false, ex.Message, null, null);
                            }
                        }

                    case "CHECK_OUT":
                        {
                            var p = req.Payload.Deserialize<CheckActionPayload>(JsonOpts);
                            if (p is null) return BridgeResponse.Fail(req.Id, "Payload không hợp lệ.");

                            try
                            {
                                await using var conn = new SqlConnection(_connectionString);
                                await conn.OpenAsync(ct);

                                var dp = new DynamicParameters();
                                dp.Add("@EmployeeCode", p.EmployeeCode);
                                dp.Add("@Pin", p.Pin);
                                dp.Add("@DeviceGuid", deviceGuid);
                                dp.Add("@EventTime", null);
                                dp.Add("@OtpCounter", null);
                                dp.Add("@ClientIp", null);
                                dp.Add("@UserAgent", "WinForms");
                                dp.Add("@Note", "WinForms");

                                var sp = await conn.QuerySingleAsync<SpApiResult>(
                                    "att.usp_CheckOut",
                                    dp,
                                    commandType: CommandType.StoredProcedure
                                );

                                if (sp == null || !sp.ok)
                                {
                                    return new BridgeResponse(req.Id, false, sp?.message ?? "Check-out thất bại.", null, sp?.errorCode);
                                }

                                return new BridgeResponse(req.Id, true, sp.message ?? "Check-out thành công.", null, null);
                            }
                            catch (SqlException ex)
                            {
                                return new BridgeResponse(req.Id, false, ex.Message, null, ex.Number);
                            }
                            catch (Exception ex)
                            {
                                return new BridgeResponse(req.Id, false, ex.Message, null, null);
                            }
                        }



                    case "DASHBOARD_GET":
                        {
                            var data = await _dashboard.GetDashboardAsync(DateOnly.FromDateTime(DateTime.Now));
                            return BridgeResponse.Ok(req.Id, "OK", data);
                        }
                    case "RECENT_ACTIVITY_GET":
                        {
                            var data = await _dashboard.GetRecentActivityAsync(DateOnly.FromDateTime(DateTime.Now), 20);
                            return BridgeResponse.Ok(req.Id, "OK", data);
                        }

                    /*   case "RECENT_ACTIVITY_GET":
                           {
                               var data = await _dashboard.GetRecentActivityAsync(20);
                               return BridgeResponse.Ok(req.Id, "OK", data);
                           }*/
                    /* case "KIOSK_QR_GET":
                        {
                            var apiBaseUrl = _config.GetValue<string>("Api:BaseUrl") ?? "http://127.0.0.1:5000";
                            var publicBaseUrl = _config.GetValue<string>("Api:PublicBaseUrl");
                            var kioskCode = _config.GetValue<string>("Kiosk:DeviceCode") ?? "KIOSK1";

                            var token = await _apiClient.GetKioskTokenAsync(kioskCode, ct);

                            string url;

                            // Nếu bạn nhập tay PublicBaseUrl (Cài đặt kết nối), ưu tiên dùng base đó cho QR.
                            var overrideBase = string.IsNullOrWhiteSpace(publicBaseUrl) ? null : publicBaseUrl.TrimEnd('/');

                            // Ưu tiên URL do API trả về, nhưng nếu có overrideBase thì thay host/port theo override.
                            if (!string.IsNullOrWhiteSpace(token.Url))
                            {
                                url = token.Url;
                                if (!string.IsNullOrWhiteSpace(overrideBase))
                                    url = ReplaceUrlBase(url, overrideBase);
                            }
                            else
                            {
                                var baseUrl = string.IsNullOrWhiteSpace(overrideBase) ? apiBaseUrl : overrideBase;
                                baseUrl = baseUrl.TrimEnd('/');

                                url = $"{baseUrl}/m/attendance.html" +
                                      $"?k={Uri.EscapeDataString(kioskCode)}" +
                                      $"&c={Uri.EscapeDataString(token.TokenC)}" +
                                      $"&s={Uri.EscapeDataString(token.TokenSig)}";
                            }

                            var qrPngBase64 = QrPngHelper.ToBase64Png(url);

                            return new BridgeResponse(req.Id, true, "OK", new
                            {
                                url,
                                expiresInSeconds = token.ExpiresInSeconds <= 0 ? 30 : token.ExpiresInSeconds,
                                qrPngBase64
                            });
                        }

*/
                    /*    case "KIOSK_QR_GET":
                            {
                                var apiBaseUrl = _config.GetValue<string>("Api:BaseUrl") ?? "http://127.0.0.1:5000";
                                var kioskCode = _config.GetValue<string>("Kiosk:DeviceCode") ?? "KIOSK1";

                                // 1) lấy token như cũ
                                var token = await _apiClient.GetKioskTokenAsync(kioskCode);

                                // 2) lấy IP LAN hiện tại
                                var lanIp = LanIpResolver.GetBestLanIPv4();

                                // 3) lấy port từ apiBaseUrl
                                var uri = new Uri(apiBaseUrl);
                                var port = uri.Port;

                                // 4) URL cho điện thoại mở
                                var url = $"http://{lanIp}:{port}/m/attendance.html" +
                                          $"?k={Uri.EscapeDataString(kioskCode)}" +
                                          $"&c={Uri.EscapeDataString(token.Code)}" +
                                          $"&s={Uri.EscapeDataString(token.Sig)}";

                                // 5) tạo QR base64 png (đúng hàm bạn có)
                                var qrPngBase64 = QrPngHelper.ToBase64Png(url);

                                return BridgeResponse.Ok(req.Id, "OK", new
                                {
                                    url,
                                    expiresInSeconds = token.ExpiresInSeconds,
                                    qrPngBase64
                                });
                            }*/
                    case "KIOSK_QR_GET":
                        {
                            var apiBaseUrl = _config.GetValue<string>("Api:BaseUrl") ?? "http://127.0.0.1:5000";
                            var publicBaseUrl = _config.GetValue<string>("Api:PublicBaseUrl");
                            var kioskCode = _config.GetValue<string>("Kiosk:DeviceCode") ?? "KIOSK1";

                            var token = await _apiClient.GetKioskTokenAsync(kioskCode, ct);

                            string url;

                            // Ưu tiên URL do API trả về.
                            // Nếu bạn đã cấu hình Api:PublicBaseUrl (qua trang Cài đặt kết nối),
                            // ta sẽ thay host/port của URL token bằng PublicBaseUrl để QR luôn đúng khi đổi IP.
                            var overrideBase = string.IsNullOrWhiteSpace(publicBaseUrl) ? null : publicBaseUrl;

                            if (!string.IsNullOrWhiteSpace(token.Url))
                            {
                                url = token.Url;
                                if (!string.IsNullOrWhiteSpace(overrideBase))
                                    url = ReplaceUrlBase(url, overrideBase!);
                            }
                            else
                            {
                                var baseUrl = (overrideBase ?? apiBaseUrl).TrimEnd('/');

                                url = $"{baseUrl}/m/attendance.html" +
                                      $"?k={Uri.EscapeDataString(kioskCode)}" +
                                      $"&c={Uri.EscapeDataString(token.TokenC)}" +
                                      $"&s={Uri.EscapeDataString(token.TokenSig)}";
                            }

                            var qrPngBase64 = QrPngHelper.ToBase64Png(url);

                            return new BridgeResponse(req.Id, true, "OK", new
                            {
                                url,
                                expiresInSeconds = token.ExpiresInSeconds <= 0 ? 30 : token.ExpiresInSeconds,
                                qrPngBase64
                            });
                        }


                    case "EMPLOYEE_LIST":
                        {
                            // payload optional
                            bool includeInactive = false;
                            if (req.Payload.ValueKind != System.Text.Json.JsonValueKind.Undefined
                                && req.Payload.ValueKind != System.Text.Json.JsonValueKind.Null)
                            {
                                var p = req.Payload.Deserialize<EmployeeListPayload>(JsonOpts);
                                includeInactive = p?.IncludeInactive ?? false;
                            }
                            var data = await _employees.ListAsync(includeInactive);
                            return BridgeResponse.Ok(req.Id, "OK", data);
                        }
                    case "EMPLOYEE_CREATE":
                        {
                            var p = req.Payload.Deserialize<EmployeeCreatePayload>(JsonOpts);
                            if (p is null) return BridgeResponse.Fail(req.Id, "Payload không hợp lệ.");
                            var created = await _employees.CreateAsync(p);
                            return BridgeResponse.Ok(req.Id, "Đã thêm nhân viên", created);
                        }
                    case "EMPLOYEE_UPDATE":
                        {
                            var p = req.Payload.Deserialize<EmployeeUpdatePayload>(JsonOpts);
                            if (p is null) return BridgeResponse.Fail(req.Id, "Payload không hợp lệ.");

                            // (Optional) chặn sớm nếu thiếu id
                            if (p.EmployeeId <= 0) return BridgeResponse.Fail(req.Id, "Thiếu EmployeeId.");

                            var updated = await _employees.UpdateAsync(p, ct);
                            return BridgeResponse.Ok(req.Id, "Đã cập nhật nhân viên.", updated);
                        }

                    case "EMPLOYEE_DELETE":
                        {
                            var p = req.Payload.Deserialize<EmployeeDeletePayload>(JsonOpts);
                            if (p is null) return BridgeResponse.Fail(req.Id, "Payload không hợp lệ.");
                            await _employees.DeleteAsync(p.EmployeeId, p.HardDelete);
                            return BridgeResponse.Ok(req.Id, p.HardDelete ? "Đã xóa vĩnh viễn" : "Đã cho nghỉ (IsActive=0)");
                        }
                    case "SHIFT_LIST":
                        {
                            var p = req.Payload.Deserialize<ShiftListPayload>(JsonOpts) ?? new ShiftListPayload(false);
                            var data = await _shiftRepo.ListAsync(p.IncludeInactive, ct);
                            return BridgeResponse.Ok(req.Id, "OK", data);
                        }

                    case "SCHEDULE_LIST":
                        {
                            var p = req.Payload.Deserialize<ScheduleListPayload>(JsonOpts);
                            if (p is null) return BridgeResponse.Fail(req.Id, "Payload không hợp lệ.");

                            var dateFrom = DateOnly.Parse(p.DateFrom);
                            var dateTo = DateOnly.Parse(p.DateTo);

                            var data = await _scheduleRepo.ListAsync(p.EmployeeId, dateFrom, dateTo, ct);
                            return BridgeResponse.Ok(req.Id, "OK", data);
                        }

                    case "SCHEDULE_UPSERT":
                        {
                            var p = JsonSerializer.Deserialize<ScheduleUpsertPayload>(
                                        req.Payload.GetRawText(), _jsonOptions
                                    )!;

                            var workDate = DateOnly.Parse(p.WorkDate);
                            var data = await _scheduleRepo.UpsertAsync(p.EmployeeId, workDate, p.ShiftId, p.Note, p.ScheduleId, ct);
                            return new BridgeResponse(req.Id, true, "OK", data, null);
                        }

                    case "SCHEDULE_DELETE_ID":
                        {
                            var p = JsonSerializer.Deserialize<ScheduleDeleteByIdPayload>(
                                        req.Payload.GetRawText(), _jsonOptions
                                    )!;

                            await _scheduleRepo.DeleteByIdAsync(p.ScheduleId, ct);
                            return new BridgeResponse(req.Id, true, "OK", null, null);
                        }

                    case "SCHEDULE_DELETE":
                        {
                            var p = JsonSerializer.Deserialize<ScheduleDeletePayload>(
                                        req.Payload.GetRawText(), _jsonOptions
                                    )!;

                            var workDate = DateOnly.Parse(p.WorkDate);
                            await _scheduleRepo.DeleteAsync(p.EmployeeId, workDate, ct);
                            return new BridgeResponse(req.Id, true, "OK", null, null);
                        }
                    case "SCHEDULE_WEEK_ALL":
                        {
                            var p = req.Payload.Deserialize<ScheduleWeekAllPayload>(JsonOpts);
                            if (p is null) return BridgeResponse.Fail(req.Id, "Payload không hợp lệ.");

                            var dateFrom = DateOnly.Parse(p.DateFrom);
                            var dateTo = DateOnly.Parse(p.DateTo);

                            var data = await _scheduleRepo.ListWeekAllAsync(dateFrom, dateTo, p.IncludeInactiveEmployees, ct);
                            return BridgeResponse.Ok(req.Id, "OK", data);
                        }

                    case "SCHEDULE_WEEK_EXPORT_EXCEL":
                        {
                            var p = req.Payload.Deserialize<ScheduleWeekExportExcelPayload>(JsonOpts);
                            if (p is null) return BridgeResponse.Fail(req.Id, "Payload không hợp lệ.");

                            if (_scheduleWeekExcel == null)
                                return BridgeResponse.Fail(req.Id, "ScheduleWeekExcelExporter chưa được khởi tạo (DI null).");

                            var dateFrom = DateOnly.Parse(p.DateFrom);
                            var dateTo = DateOnly.Parse(p.DateTo);

                            if (dateTo < dateFrom)
                                return BridgeResponse.Fail(req.Id, "dateTo phải >= dateFrom.");

                            var defaultName = $"ChamCong_LichTuan_{dateFrom:yyyy_MM_dd}_to_{dateTo:yyyy_MM_dd}.xlsx";
                            var chosen = await PromptSaveExcelPathAsync(defaultName);

                            if (string.IsNullOrWhiteSpace(chosen))
                                return BridgeResponse.Ok(req.Id, "CANCELLED", new { cancelled = true });

                            var path = await _scheduleWeekExcel.ExportWeekAllAsync(dateFrom, dateTo, p.IncludeInactiveEmployees, chosen!, ct);
                            return BridgeResponse.Ok(req.Id, "OK", new { path });
                        }



                    case "PAYROLL_PREVIEW":
                        {
                            var p = req.Payload.Deserialize<PayrollPreviewPayload>(JsonOpts);
                            if (p is null) return BridgeResponse.Fail(req.Id, "Payload không hợp lệ.");

                            var dateFrom = DateOnly.Parse(p.DateFrom);
                            var dateTo = DateOnly.Parse(p.DateTo);

                            if (dateTo < dateFrom)
                                return BridgeResponse.Fail(req.Id, "DateTo phải >= DateFrom.");

                            var rows = await _payroll.GetPreviewAsync(dateFrom, dateTo, p.EmployeeId, ct);
                            return BridgeResponse.Ok(req.Id, "OK", rows);
                        }

                    case "PAYROLL_EXPORT_EXCEL":
                        {
                            var p = req.Payload.Deserialize<PayrollExportExcelPayload>(JsonOpts);
                            if (p is null) return BridgeResponse.Fail(req.Id, "Payload không hợp lệ.");

                            var dateFrom = DateOnly.Parse(p.DateFrom);
                            var dateTo = DateOnly.Parse(p.DateTo);

                            if (dateTo < dateFrom)
                                return BridgeResponse.Fail(req.Id, "DateTo phải >= DateFrom.");

                            // Giống xuất Excel lịch tuần: cho user chọn đường dẫn lưu file
                            /*var scope = p.EmployeeId.HasValue ? $"EMP_{p.EmployeeId.Value}" : "ALL";
                            var defaultName = $"ChamCong_Luong_{dateFrom:yyyy_MM_dd}_to_{dateTo:yyyy_MM_dd}_{scope}.xlsx";*/
                            var defaultName = await _payrollExcel.SuggestFileNameAsync(dateFrom, dateTo, p.EmployeeId, ct);

                            var chosen = await PromptSaveExcelPathAsync(defaultName);

                            if (string.IsNullOrWhiteSpace(chosen))
                                return BridgeResponse.Ok(req.Id, "CANCELLED", new { cancelled = true });

                            var path = await _payrollExcel.ExportToPathAsync(
                                dateFrom, dateTo, chosen!, p.SplitToSheetsByEmployee, p.EmployeeId, ct);

                            return BridgeResponse.Ok(req.Id, "OK", new { path });
                        }


                    // Pay multiplier (nhân đôi/nhân ba lương theo ngày)
                    case "PAY_MULTIPLIER_LIST":
                        {
                            var p = req.Payload.Deserialize<PayMultiplierListPayload>(JsonOpts);
                            if (p is null) return BridgeResponse.Fail(req.Id, "Payload không hợp lệ.");

                            var dateFrom = DateOnly.Parse(p.DateFrom);
                            var dateTo = DateOnly.Parse(p.DateTo);
                            if (dateTo < dateFrom)
                                return BridgeResponse.Fail(req.Id, "DateTo phải >= DateFrom.");

                            var data = await _payMultiplierRepo.ListAsync(dateFrom, dateTo, ct);
                            return BridgeResponse.Ok(req.Id, "OK", data);
                        }

                    case "PAY_MULTIPLIER_UPSERT":
                        {
                            var p = req.Payload.Deserialize<PayMultiplierUpsertPayload>(JsonOpts);
                            if (p is null) return BridgeResponse.Fail(req.Id, "Payload không hợp lệ.");

                            var workDate = DateOnly.Parse(p.WorkDate);
                            if (p.Multiplier <= 0 || p.Multiplier > 10)
                                return BridgeResponse.Fail(req.Id, "Multiplier must be between 0 and 10.");

                            await _payMultiplierRepo.UpsertAsync(workDate, p.Multiplier, p.Note, ct);
                            return BridgeResponse.Ok(req.Id, "OK");
                        }

                    case "PAY_MULTIPLIER_DELETE":
                        {
                            var p = req.Payload.Deserialize<PayMultiplierDeletePayload>(JsonOpts);
                            if (p is null) return BridgeResponse.Fail(req.Id, "Payload không hợp lệ.");

                            var workDate = DateOnly.Parse(p.WorkDate);
                            await _payMultiplierRepo.DeleteAsync(workDate, ct);
                            return BridgeResponse.Ok(req.Id, "OK");
                        }


                    // Admin PIN (unlock protected modules)
                    case "ADMIN_PIN_STATUS":
                        {
                            var has = await _adminPin.HasPinAsync(ct);
                            var dbName = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_connectionString).InitialCatalog;
                            return BridgeResponse.Ok(req.Id, "OK", new { hasPin = has, defaultPin = _adminPin.DefaultPin, dbName });
                        }

                    case "ADMIN_PIN_VERIFY":
                        {
                            var p = req.Payload.Deserialize<AdminPinVerifyPayload>(JsonOpts);
                            if (p is null) return BridgeResponse.Fail(req.Id, "Payload không hợp lệ.");
                            var ok = await _adminPin.VerifyAsync(p.Pin, ct);
                            return BridgeResponse.Ok(req.Id, ok ? "OK" : "PIN không đúng", new { ok });
                        }

                    case "ADMIN_PIN_CHANGE":
                        {
                            var p = req.Payload.Deserialize<AdminPinChangePayload>(JsonOpts);
                            if (p is null) return BridgeResponse.Fail(req.Id, "Payload không hợp lệ.");
                            await _adminPin.ChangeAsync(p.CurrentPin, p.NewPin, ct);
                            return BridgeResponse.Ok(req.Id, "Đã đổi PIN thành công");
                        }

                    case "ADMIN_PIN_RESET_DEFAULT":
                        {
                            await _adminPin.ResetToDefaultAsync(ct);
                            return BridgeResponse.Ok(req.Id, $"Đã reset PIN về mặc định ({_adminPin.DefaultPin}).");
                        }

                    case "ADMIN_PIN_RESET_RECOVERY":
                        {
                            var p = req.Payload.Deserialize<AdminPinResetRecoveryPayload>(JsonOpts);
                            if (p is null) return BridgeResponse.Fail(req.Id, "Payload không hợp lệ.");
                            var r = await _adminPin.ResetWithRecoveryCodeAsync(p.RecoveryCode, p.NewPin, ct);
                            if (!r.Ok) return BridgeResponse.Fail(req.Id, r.Message);
                            return BridgeResponse.Ok(req.Id, r.Message);
                        }
                    default:
                        return BridgeResponse.Fail(req.Id, "Unknown request type");
                }
            }
            catch (AppDomainException ex)
            {
                return BridgeResponse.Fail(req.Id, ex.Message, ex.ErrorCode);
            }
            /*      catch (Exception ex)
                  {
                      _logger.LogError(ex, "Dispatch error: {Type}", req.Type);
                      return BridgeResponse.Fail(req.Id, "Lỗi hệ thống (xem log).");
                  }*/
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dispatch error: {Type}", req.Type);
#if DEBUG
                return BridgeResponse.Fail(req.Id, ex.Message);   // hiện lỗi thật lên UI
#else
                                    return BridgeResponse.Fail(req.Id, "Lỗi hệ thống (xem log).");
#endif
            }

        }
        private bool _allowClose = false;

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_allowClose)
            {
                base.OnFormClosing(e);
                return;
            }

            if (e.CloseReason == CloseReason.WindowsShutDown ||
                e.CloseReason == CloseReason.TaskManagerClosing)
            {
                base.OnFormClosing(e);
                return;
            }

            var result = MessageBox.Show(
                "Bạn có chắc muốn thoát ứng dụng không?",
                "Xác nhận thoát",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _allowClose = true;
            base.OnFormClosing(e);

            // Nếu muốn thoát hết mọi form:
            // Application.Exit();
        }

    }
}