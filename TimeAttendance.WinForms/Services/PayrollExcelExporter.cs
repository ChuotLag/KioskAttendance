using ClosedXML.Excel;
using Dapper;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Globalization;
using System.Text;
using TimeAttendance.WinForms.Core;
using TimeAttendance.WinForms.Infrastructure;

namespace TimeAttendance.WinForms.Services;

public interface IPayrollExcelExporter
{
    /// <summary>
    /// Export payroll report into an .xlsx file.
    /// The workbook contains:
    /// - "TONG_HOP" sheet: totals of all employees in the date range
    /// - One sheet per employee: daily detail with check-in/out
    /// </summary>
    Task<string> ExportAsync(DateOnly dateFrom, DateOnly dateTo, string outputDir, bool splitByEmployee, long? employeeId, CancellationToken ct);

    /// <summary>
    /// Export payroll report to a specific full file path (SaveFileDialog scenario).
    /// </summary>
    Task<string> ExportToPathAsync(DateOnly dateFrom, DateOnly dateTo, string outputPath, bool splitByEmployee, long? employeeId, CancellationToken ct);

    /// <summary>
    /// Export previous month (from day 1 to last day) based on local time.
    /// </summary>
    Task<string> ExportLastMonthAsync(string outputDir, bool splitByEmployee, CancellationToken ct);

    Task<string> SuggestFileNameAsync(DateOnly dateFrom, DateOnly dateTo, long? employeeId, CancellationToken ct);


}

public sealed class PayrollExcelExporter : IPayrollExcelExporter
{
    private readonly ISqlConnectionFactory _db;
    private readonly ILogger<PayrollExcelExporter> _logger;

    public PayrollExcelExporter(ISqlConnectionFactory db, ILogger<PayrollExcelExporter> logger)
    {
        _db = db;
        _logger = logger;
    }
    public async Task<string> SuggestFileNameAsync(DateOnly dateFrom, DateOnly dateTo, long? employeeId, CancellationToken ct)
    {
        var rows = await LoadRowsAsync(dateFrom, dateTo, employeeId, ct);

        var scope = employeeId.HasValue
            ? GetEmployeeScope(rows, employeeId.Value)   // <-- hàm này đã ưu tiên FullName của bạn
            : "ALL";

        return $"Luong_{dateFrom:yyyy_MM_dd}_to_{dateTo:yyyy_MM_dd}_{scope}.xlsx";
    }

    public Task<string> ExportLastMonthAsync(string outputDir, bool splitByEmployee, CancellationToken ct)
    {
        var now = DateTime.Now;
        var firstThisMonth = new DateTime(now.Year, now.Month, 1);
        var from = DateOnly.FromDateTime(firstThisMonth.AddMonths(-1));
        var to = DateOnly.FromDateTime(firstThisMonth.AddDays(-1));
        return ExportAsync(from, to, outputDir, splitByEmployee, null, ct);
    }

    public async Task<string> ExportAsync(DateOnly dateFrom, DateOnly dateTo, string outputDir, bool splitByEmployee, long? employeeId, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);

        var rows = await LoadRowsAsync(dateFrom, dateTo, employeeId, ct);

        var scope = employeeId.HasValue ? GetEmployeeScope(rows, employeeId.Value) : "ALL";
        var fileName = $"ChamCong_Luong_{dateFrom:yyyy_MM_dd}_to_{dateTo:yyyy_MM_dd}_{scope}.xlsx";
        var fullPath = Path.Combine(outputDir, fileName);

        return await ExportToPathInternalAsync(rows, dateFrom, dateTo, fullPath, splitByEmployee, ct);
    }

    public async Task<string> ExportToPathAsync(DateOnly dateFrom, DateOnly dateTo, string outputPath, bool splitByEmployee, long? employeeId, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(outputPath);
        Directory.CreateDirectory(string.IsNullOrWhiteSpace(dir) ? Environment.CurrentDirectory : dir);

        var rows = await LoadRowsAsync(dateFrom, dateTo, employeeId, ct);

        return await ExportToPathInternalAsync(rows, dateFrom, dateTo, outputPath, splitByEmployee, ct);
    }

    private async Task<string> ExportToPathInternalAsync(List<PayrollPreviewDto> rows, DateOnly dateFrom, DateOnly dateTo, string outputPath, bool splitByEmployee, CancellationToken ct)
    {
        // ct currently only used in LoadRowsAsync; here we just build workbook
        using var wb = new XLWorkbook();

        AddSummarySheet(wb, rows, dateFrom, dateTo);

        if (splitByEmployee)
            AddEmployeeSheets(wb, rows, dateFrom, dateTo);

        wb.SaveAs(outputPath);
        _logger.LogInformation("Exported payroll excel: {Path}", outputPath);
        return outputPath;
    }

    private async Task<List<PayrollPreviewDto>> LoadRowsAsync(DateOnly dateFrom, DateOnly dateTo, long? employeeId, CancellationToken ct)
    {
        // Query directly from view pay.vw_PayrollPreview
        const string sql = @"
SELECT
    EmployeeId,
    EmployeeCode,
    FullName,
    WorkDate,
    ShiftCode,
    CheckInTime,
    CheckOutTime,
    MinutesWorked,
    LateMinutes,
    GrossPay,
    PenaltyAmount,
    NetPay
FROM pay.vw_PayrollPreview
WHERE WorkDate >= @DateFrom
  AND WorkDate <= @DateTo
  AND (@EmployeeId IS NULL OR EmployeeId = @EmployeeId)
ORDER BY EmployeeCode ASC, WorkDate ASC;";

        using var conn = _db.Create();
        var result = await conn.QueryAsync<PayrollPreviewDto>(
            new CommandDefinition(
                sql,
                new
                {
                    DateFrom = dateFrom.ToDateTime(TimeOnly.MinValue),
                    DateTo = dateTo.ToDateTime(TimeOnly.MinValue),
                    EmployeeId = employeeId
                },
                cancellationToken: ct,
                commandType: CommandType.Text
            )
        );

        return result.ToList();
    }

    private static void AddSummarySheet(XLWorkbook wb, List<PayrollPreviewDto> rows, DateOnly dateFrom, DateOnly dateTo)
    {
        var ws = wb.Worksheets.Add("TONG_HOP");

        ws.Cell(1, 1).Value = "BÁO CÁO CHẤM CÔNG - TIỀN LƯƠNG";
        ws.Cell(2, 1).Value = $"Từ {dateFrom:yyyy-MM-dd} đến {dateTo:yyyy-MM-dd}";

        ws.Range(1, 1, 1, 8).Merge().Style.Font.SetBold().Font.SetFontSize(16);
        ws.Range(2, 1, 2, 8).Merge().Style.Font.SetBold().Font.SetFontSize(11);
        ws.Row(1).Height = 24;

        // Header
        var startRow = 4;
        ws.Cell(startRow, 1).Value = "Mã NV";
        ws.Cell(startRow, 2).Value = "Họ tên";
        ws.Cell(startRow, 3).Value = "Tổng giờ";
        ws.Cell(startRow, 4).Value = "Tổng trễ (phút)";
        ws.Cell(startRow, 5).Value = "Tổng";
        ws.Cell(startRow, 6).Value = "Phạt";
        ws.Cell(startRow, 7).Value = "Thực nhận";
        ws.Cell(startRow, 8).Value = "Số dòng";

        ws.Range(startRow, 1, startRow, 8).Style.Font.SetBold();
        ws.Range(startRow, 1, startRow, 8).Style.Fill.SetBackgroundColor(XLColor.FromHtml("#E7F2F3"));
        ws.Range(startRow, 1, startRow, 8).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(startRow, 1, startRow, 8).Style.Border.InsideBorder = XLBorderStyleValues.Thin;

        var groups = rows
            .GroupBy(r => new { r.EmployeeId, r.EmployeeCode, r.FullName })
            .OrderBy(g => g.Key.EmployeeCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rIdx = startRow + 1;
        foreach (var g in groups)
        {
            var totalMinutes = g.Sum(x => x.MinutesWorked);
            var totalLate = g.Sum(x => x.LateMinutes);
            var gross = g.Sum(x => x.GrossPay);
            var penalty = g.Sum(x => x.PenaltyAmount);
            var net = g.Sum(x => x.NetPay);

            ws.Cell(rIdx, 1).Value = g.Key.EmployeeCode;
            ws.Cell(rIdx, 2).Value = g.Key.FullName;
            ws.Cell(rIdx, 3).Value = Math.Round(totalMinutes / 60m, 2);
            ws.Cell(rIdx, 4).Value = totalLate;
            ws.Cell(rIdx, 5).Value = gross;
            ws.Cell(rIdx, 6).Value = penalty;
            ws.Cell(rIdx, 7).Value = net;
            ws.Cell(rIdx, 8).Value = g.Count();
            rIdx++;
        }

        // Totals
        ws.Cell(rIdx, 1).Value = "TỔNG";
        ws.Range(rIdx, 1, rIdx, 2).Merge().Style.Font.SetBold();
        ws.Cell(rIdx, 3).Value = Math.Round(rows.Sum(x => x.MinutesWorked) / 60m, 2);
        ws.Cell(rIdx, 4).Value = rows.Sum(x => x.LateMinutes);
        ws.Cell(rIdx, 5).Value = rows.Sum(x => x.GrossPay);
        ws.Cell(rIdx, 6).Value = rows.Sum(x => x.PenaltyAmount);
        ws.Cell(rIdx, 7).Value = rows.Sum(x => x.NetPay);
        ws.Cell(rIdx, 8).Value = rows.Count;
        ws.Range(rIdx, 1, rIdx, 8).Style.Fill.SetBackgroundColor(XLColor.FromHtml("#F3F4F6"));
        ws.Range(rIdx, 1, rIdx, 8).Style.Font.SetBold();

        // Formatting
        ws.Column(3).Style.NumberFormat.Format = "0.00"; // hours
        ws.Column(4).Style.NumberFormat.Format = "0";
        ws.Columns(5, 7).Style.NumberFormat.Format = "#,##0";

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(startRow);
    }

    private static void AddEmployeeSheets(XLWorkbook wb, List<PayrollPreviewDto> rows, DateOnly dateFrom, DateOnly dateTo)
    {
        var groups = rows
            .GroupBy(r => new { r.EmployeeId, r.EmployeeCode, r.FullName })
            .OrderBy(g => g.Key.EmployeeCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var g in groups)
        {
            var sheetName = MakeSafeSheetName($"{g.Key.FullName}");
            var ws = wb.Worksheets.Add(sheetName);

            //ws.Cell(1, 1).Value = $"CHI TIẾT: {g.Key.EmployeeCode} - {g.Key.FullName}";
            ws.Cell(1, 1).Value = $"CHI TIẾT: {g.Key.FullName}";
            ws.Cell(2, 1).Value = $"Từ {dateFrom:yyyy-MM-dd} đến {dateTo:yyyy-MM-dd}";
            ws.Range(1, 1, 1, 11).Merge().Style.Font.SetBold().Font.SetFontSize(14);
            ws.Range(2, 1, 2, 11).Merge().Style.Font.SetBold().Font.SetFontSize(11);
            ws.Row(1).Height = 22;

            var startRow = 4;
            ws.Cell(startRow, 1).Value = "Ngày";
            ws.Cell(startRow, 2).Value = "Ca";
            ws.Cell(startRow, 3).Value = "Check-in";
            ws.Cell(startRow, 4).Value = "Check-out";
            ws.Cell(startRow, 5).Value = "Giờ làm";
            ws.Cell(startRow, 6).Value = "Trễ (phút)";
            ws.Cell(startRow, 7).Value = "Tổng";
            ws.Cell(startRow, 8).Value = "Phạt";
            ws.Cell(startRow, 9).Value = "Thực nhận";
            ws.Cell(startRow, 10).Value = "Số phút làm việc";
            ws.Cell(startRow, 11).Value = "Ghi chú";

            ws.Range(startRow, 1, startRow, 11).Style.Font.SetBold();
            ws.Range(startRow, 1, startRow, 11).Style.Fill.SetBackgroundColor(XLColor.FromHtml("#E7F2F3"));
            ws.Range(startRow, 1, startRow, 11).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            ws.Range(startRow, 1, startRow, 11).Style.Border.InsideBorder = XLBorderStyleValues.Thin;

            var rIdx = startRow + 1;
            foreach (var x in g.OrderBy(x => x.WorkDate))
            {
                ws.Cell(rIdx, 1).Value = x.WorkDate.Date;
                ws.Cell(rIdx, 2).Value = x.ShiftCode;
                ws.Cell(rIdx, 3).Value = x.CheckInTime;
                ws.Cell(rIdx, 4).Value = x.CheckOutTime;
                ws.Cell(rIdx, 5).Value = Math.Round(x.MinutesWorked / 60m, 2);
                ws.Cell(rIdx, 6).Value = x.LateMinutes;
                ws.Cell(rIdx, 7).Value = x.GrossPay;
                ws.Cell(rIdx, 8).Value = x.PenaltyAmount;
                ws.Cell(rIdx, 9).Value = x.NetPay;
                ws.Cell(rIdx, 10).Value = x.MinutesWorked;
                ws.Cell(rIdx, 11).Value = "";
                rIdx++;
            }

            // Totals line
            ws.Cell(rIdx, 1).Value = "TỔNG";
            ws.Range(rIdx, 1, rIdx, 4).Merge().Style.Font.SetBold();
            ws.Cell(rIdx, 5).Value = Math.Round(g.Sum(x => x.MinutesWorked) / 60m, 2);
            ws.Cell(rIdx, 6).Value = g.Sum(x => x.LateMinutes);
            ws.Cell(rIdx, 7).Value = g.Sum(x => x.GrossPay);
            ws.Cell(rIdx, 8).Value = g.Sum(x => x.PenaltyAmount);
            ws.Cell(rIdx, 9).Value = g.Sum(x => x.NetPay);
            ws.Range(rIdx, 1, rIdx, 11).Style.Fill.SetBackgroundColor(XLColor.FromHtml("#F3F4F6"));
            ws.Range(rIdx, 1, rIdx, 11).Style.Font.SetBold();

            // Formatting
            ws.Column(1).Style.DateFormat.Format = "yyyy-MM-dd";
            ws.Columns(3, 4).Style.DateFormat.Format = "HH:mm";
            ws.Column(5).Style.NumberFormat.Format = "0.00";
            ws.Column(6).Style.NumberFormat.Format = "0";
            ws.Columns(7, 9).Style.NumberFormat.Format = "#,##0";
            ws.Column(10).Style.NumberFormat.Format = "0";

            ws.Columns().AdjustToContents();
            ws.SheetView.FreezeRows(startRow);
        }
    }

/*    private static string GetEmployeeScope(List<PayrollPreviewDto> rows, long employeeId)
    {

        var first = rows.FirstOrDefault();
        if (first is not null && !string.IsNullOrWhiteSpace(first.EmployeeCode))
            return SanitizeFilePart(first.EmployeeCode);

        return $"EMP_{employeeId}";
    }*/

    private static string GetEmployeeScope(List<PayrollPreviewDto> rows, long employeeId)
    {
        var first = rows.FirstOrDefault();

        // Ưu tiên tên nhân viên
        if (first is not null && !string.IsNullOrWhiteSpace(first.FullName))
            return SanitizeFilePart(first.FullName);

        // Fallback: nếu không có tên thì dùng mã nhân viên
        if (first is not null && !string.IsNullOrWhiteSpace(first.EmployeeCode))
            return SanitizeFilePart(first.EmployeeCode);

        // Fallback cuối: dùng ID
        return $"EMP_{employeeId}";
    }


    private static string SanitizeFilePart(string input)
    {
        // Keep it simple: alnum, dash, underscore
        var sb = new StringBuilder();
        foreach (var ch in input.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_') sb.Append(ch);
        }
        var s = sb.ToString();
        return string.IsNullOrWhiteSpace(s) ? "EMP" : s;
    }

    private static string MakeSafeSheetName(string input)
    {
        // Excel sheet name max 31 chars; disallow: : \/ ? * [ ]
        var invalid = new HashSet<char>(new[] { ':', '\\', '/', '?', '*', '[', ']' });
        var sb = new StringBuilder();
        foreach (var ch in input)
        {
            if (invalid.Contains(ch)) continue;
            if (char.IsControl(ch)) continue;
            sb.Append(ch);
        }
        var s = sb.ToString().Trim();
        if (string.IsNullOrWhiteSpace(s)) s = "NV";
        if (s.Length > 31) s = s.Substring(0, 31);
        return s;
    }
}
