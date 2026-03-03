using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;

namespace TimeAttendance.WinForms.Services;

public interface IMonthlyPayrollExportScheduler : IDisposable
{
    void Start();
}

/// <summary>
/// Runs inside the WinForms app. It will export last month's payroll report
/// at configured DayOfMonth/Hour/Minute. Note: it only runs while the app is running.
/// </summary>
public sealed class MonthlyPayrollExportScheduler : IMonthlyPayrollExportScheduler
{
    private readonly IPayrollExcelExporter _exporter;
    private readonly IConfiguration _cfg;
    private readonly ILogger<MonthlyPayrollExportScheduler> _logger;
    private System.Threading.Timer? _timer;
    private volatile int _isRunning = 0;

    public MonthlyPayrollExportScheduler(
        IPayrollExcelExporter exporter,
        IConfiguration cfg,
        ILogger<MonthlyPayrollExportScheduler> logger)
    {
        _exporter = exporter;
        _cfg = cfg;
        _logger = logger;
    }

    public void Start()
    {
        var enabled = _cfg.GetValue("Export:Enabled", true);
        if (!enabled)
        {
            _logger.LogInformation("Monthly export scheduler disabled (Export:Enabled=false)");
            return;
        }

        // Tick every minute
        _timer = new System.Threading.Timer(async _ => await TickAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1));
        _logger.LogInformation("Monthly export scheduler started");
    }

    private async Task TickAsync()
    {
        // Avoid overlapping
        if (Interlocked.Exchange(ref _isRunning, 1) == 1) return;
        try
        {
            var enabled = _cfg.GetValue("Export:Enabled", true);
            if (!enabled) return;

            var day = _cfg.GetValue("Export:DayOfMonth", 10);
            var hour = _cfg.GetValue("Export:Hour", 7);
            var minute = _cfg.GetValue("Export:Minute", 0);

            var now = DateTime.Now;
            if (now.Day != day || now.Hour != hour || now.Minute != minute) return;

            // Export previous month only once per month (persisted)
            var period = GetPreviousMonthKey(now); // yyyy-MM
            var outputDir = ResolveOutputDir();
            var markerPath = Path.Combine(outputDir, ".last_export_period.txt");
            Directory.CreateDirectory(outputDir);

            var last = File.Exists(markerPath) ? (File.ReadAllText(markerPath) ?? "").Trim() : "";
            if (string.Equals(last, period, StringComparison.OrdinalIgnoreCase))
                return;

            _logger.LogInformation("Running monthly payroll export for period {Period}", period);
            var path = await _exporter.ExportLastMonthAsync(outputDir, splitByEmployee: true, CancellationToken.None);
            File.WriteAllText(markerPath, period);

            // Append log
            File.AppendAllText(Path.Combine(outputDir, "export_log.txt"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Export OK ({period}): {path}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            try
            {
                var outputDir = ResolveOutputDir();
                Directory.CreateDirectory(outputDir);
                File.AppendAllText(Path.Combine(outputDir, "export_log.txt"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Export ERROR: {ex}{Environment.NewLine}");
            }
            catch { /* ignore */ }

            _logger.LogError(ex, "Monthly payroll export failed");
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    private string ResolveOutputDir()
    {
        var configured = (_cfg["Export:OutputDir"] ?? "").Trim();
        if (!string.IsNullOrEmpty(configured)) return configured;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ChamCong", "Exports");
    }

    private static string GetPreviousMonthKey(DateTime now)
    {
        var firstThisMonth = new DateTime(now.Year, now.Month, 1);
        var prev = firstThisMonth.AddMonths(-1);
        return prev.ToString("yyyy-MM");
    }

    public void Dispose() => _timer?.Dispose();
}
