using Microsoft.Extensions.Configuration;
using System.Net.Http;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using System.Windows.Forms;
using TimeAttendance.WinForms.Infrastructure;
using TimeAttendance.WinForms.Services;

namespace TimeAttendance.WinForms
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // ====== LOG DIR (works after exporting exe) ======
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ChamCong",
                "logs");
            Directory.CreateDirectory(logDir);

            // ====== SERILOG (WinForms) ======
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.File(
                    path: Path.Combine(logDir, "winforms-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    fileSizeLimitBytes: 10_000_000,
                    rollOnFileSizeLimit: true,
                    shared: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1))
                .CreateLogger();

            try
            {
                ApplicationConfiguration.Initialize();

                var services = new ServiceCollection();

                // Configuration
                // Load default appsettings.json + optional override file in %AppData%\ChamCong\appsettings.local.json
                var localCfgPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ChamCong",
                    "appsettings.local.json");

                var config = new ConfigurationBuilder()
                    .SetBasePath(AppContext.BaseDirectory)
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                    .AddJsonFile(localCfgPath, optional: true, reloadOnChange: true)
                    .Build();



                // ====== START API (hidden) ======
                // API exe must be next to WinForms exe. We wait until /health is OK.
                var apiBaseUrl = config["Api:BaseUrl"] ?? "http://localhost:5000";
                try
                {
                    EnsureApiStartedHiddenAsync(apiBaseUrl).GetAwaiter().GetResult();
                    Log.Information("API is ready: {ApiBaseUrl}", apiBaseUrl);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to start API");
                    MessageBox.Show(
                        $"Không khởi động được API.\n\n{ex.Message}\n\nXem log: %AppData%\\ChamCong\\logs\\api-*.log",
                        "Lỗi khởi động",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                    return;
                }

                services.AddSingleton<IConfiguration>(config);

                // Logging (bridge ILogger -> Serilog)
                services.AddLogging(b =>
                {
                    b.ClearProviders();
                    b.AddSerilog(Log.Logger, dispose: true);
                    b.SetMinimumLevel(LogLevel.Information);
                });

                Log.Information("=== WINFORMS START ===");
                Log.Information("BaseDir: {BaseDir}", AppContext.BaseDirectory);
                Log.Information("Ui:Mode={UiMode}", config["Ui:Mode"]);
                Log.Information("Api:BaseUrl={ApiBaseUrl}", config["Api:BaseUrl"]);

                // Infrastructure
                services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
                services.AddSingleton<IAttendanceRepository, AttendanceRepository>();
                services.AddSingleton<IDashboardRepository, DashboardRepository>();
                services.AddSingleton<IKioskRepository, KioskRepository>();
                services.AddHttpClient<TimeAttendance.WinForms.Infrastructure.IKioskApiClient,
                            TimeAttendance.WinForms.Infrastructure.KioskApiClient>((sp, http) =>
                            {
                                var cfg = sp.GetRequiredService<IConfiguration>();
                                http.BaseAddress = new Uri(cfg["Api:BaseUrl"] ?? "http://192.168.1.62:5000");
                                http.Timeout = TimeSpan.FromSeconds(5);
                            });
                services.AddSingleton<IEmployeeRepository, EmployeeRepository>();
                services.AddSingleton<IPayrollRepository, PayrollRepository>();

                // Admin PIN (unlock protected modules)
                services.AddSingleton<IAdminPinRepository, AdminPinRepository>();

                // Export (Excel)
                services.AddSingleton<IPayrollExcelExporter, PayrollExcelExporter>();
                services.AddSingleton<IMonthlyPayrollExportScheduler, MonthlyPayrollExportScheduler>();

                // Export (Excel) - Weekly schedule
                services.AddSingleton<IScheduleWeekExcelExporter, ScheduleWeekExcelExporter>();

                // UI
                services.AddSingleton<MainForm>();
                services.AddSingleton<AttendanceKioskForm>();

                var sp = services.BuildServiceProvider();

                // Start monthly scheduler (runs only while app is open)
                using var scheduler = sp.GetRequiredService<IMonthlyPayrollExportScheduler>();
                scheduler.Start();

                // Choose UI mode via appsettings.json: Ui:Mode = "Pin" | "Web" (default: Pin)
                var uiMode = (config["Ui:Mode"] ?? "Pin").Trim();
                if (uiMode.Equals("Web", StringComparison.OrdinalIgnoreCase))
                {
                    Application.Run(sp.GetRequiredService<MainForm>());
                }
                else
                {
                    Application.Run(sp.GetRequiredService<AttendanceKioskForm>());
                }

                Log.Information("=== WINFORMS END (normal) ===");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "WINFORMS crashed");
                throw;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        private static async Task<bool> IsApiUpAsync(string baseUrl)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                var url = $"{baseUrl.TrimEnd('/')}/health";
                var res = await http.GetAsync(url);
                return res.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /*private static async Task EnsureApiStartedHiddenAsync(string baseUrl, CancellationToken ct = default)
        {
            // If API is already up, do nothing
            if (await IsApiUpAsync(baseUrl)) return;

            var exeDir = AppContext.BaseDirectory;

            // API exe should be placed next to WinForms exe
            var candidates = new[]
            {
        Path.Combine(exeDir, "TimeAttendance.Api.exe"),
        Path.Combine(exeDir, "TimeAttendance.Api")
    };

            var apiExe = candidates.FirstOrDefault(File.Exists);
            if (apiExe is null)
                throw new FileNotFoundException("Không tìm thấy TimeAttendance.Api.exe (cần đặt cùng thư mục với WinForms.exe).", Path.Combine(exeDir, "TimeAttendance.Api.exe"));

            var psi = new ProcessStartInfo
            {
                FileName = apiExe,
                WorkingDirectory = exeDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(psi);

            // Wait for API to be ready (max 10s)
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (await IsApiUpAsync(baseUrl)) return;
                await Task.Delay(300, ct);
            }

            throw new TimeoutException("Không khởi động được API (timeout). Kiểm tra port 5000 hoặc mở file log api-*.log.");
        }*/


        private static async Task EnsureApiStartedHiddenAsync(string baseUrl, CancellationToken ct = default)
        {
            // Nếu API đã chạy thì thôi
            if (await IsApiUpAsync(baseUrl)) return;

            var exeDir = AppContext.BaseDirectory;

            // ✅ Ưu tiên API trong thư mục con API_LAN
            var apiDir = Path.Combine(exeDir, "API_LAN");
            var apiExe = Path.Combine(apiDir, "TimeAttendance.Api.exe");
            var apiDll = Path.Combine(apiDir, "TimeAttendance.Api.dll");

            ProcessStartInfo psi;

            if (File.Exists(apiExe))
            {
                psi = new ProcessStartInfo
                {
                    FileName = apiExe,
                    WorkingDirectory = apiDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
            }
            else if (File.Exists(apiDll))
            {
                psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"\"{apiDll}\"",
                    WorkingDirectory = apiDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
            }
            else
            {
                throw new FileNotFoundException(
                    "Không tìm thấy API trong thư mục con API_LAN (cần TimeAttendance.Api.exe hoặc TimeAttendance.Api.dll).",
                    apiExe);
            }

            // ✅ Đảm bảo API listen đúng port (và nghe LAN nếu cần)
            psi.Environment["ASPNETCORE_URLS"] = "http://0.0.0.0:5000";

            // ✅ Ghi log để khỏi “timeout mù”
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ChamCong", "logs");
            Directory.CreateDirectory(logDir);

            var outLog = Path.Combine(logDir, "api-stdout.log");
            var errLog = Path.Combine(logDir, "api-stderr.log");

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) File.AppendAllText(outLog, e.Data + Environment.NewLine);
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) File.AppendAllText(errLog, e.Data + Environment.NewLine);
            };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            // ✅ Chờ lâu hơn (máy yếu/khởi động lần đầu thường > 10s)
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (await IsApiUpAsync(baseUrl)) return;
                await Task.Delay(300, ct);
            }

            throw new TimeoutException("Không khởi động được API (timeout). Kiểm tra port 5000 hoặc mở log api-stdout/err.log.");
        }


    }
}