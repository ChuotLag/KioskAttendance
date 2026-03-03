using ClosedXML.Excel;
using Dapper;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Text.RegularExpressions;
using TimeAttendance.WinForms.Core;
using TimeAttendance.WinForms.Infrastructure;

namespace TimeAttendance.WinForms.Services;

public interface IScheduleWeekExcelExporter
{
    /// <summary>
    /// Export weekly schedule (all employees) into an .xlsx file at outputPath.
    /// Layout: rows = employees, columns = Mon..Sun.
    /// Each cell can contain multiple shifts (one per line block).
    /// </summary>
    Task<string> ExportWeekAllAsync(DateOnly dateFrom, DateOnly dateTo, bool includeInactiveEmployees, string outputPath, CancellationToken ct);
}

public sealed class ScheduleWeekExcelExporter : IScheduleWeekExcelExporter
{
    private readonly ISqlConnectionFactory _db;
    private readonly ILogger<ScheduleWeekExcelExporter> _logger;

    public ScheduleWeekExcelExporter(ISqlConnectionFactory db, ILogger<ScheduleWeekExcelExporter> logger)
    {
        _db = db;
        _logger = logger;
    }

    private sealed class ScheduleWeekRowDto
    {
        public long? ScheduleId { get; set; }
        public long EmployeeId { get; set; }
        public DateTime WorkDate { get; set; }
        public int ShiftId { get; set; }
        public string? ShiftCode { get; set; }
        public string? ShiftName { get; set; }
        /*        public TimeSpan? StartTime { get; set; }
                public TimeSpan? EndTime { get; set; }*/
        public object? StartTime { get; set; }
        public object? EndTime { get; set; }

        public string? Note { get; set; }
    }
    private static TimeSpan? ParseTime(object? value)
    {
        if (value is null) return null;

        if (value is TimeSpan ts) return ts;
        if (value is DateTime dt) return dt.TimeOfDay;

        var s = value.ToString()?.Trim();
        if (string.IsNullOrEmpty(s)) return null;

        // chấp nhận "06:00" hoặc "06:00:00"
        if (TimeSpan.TryParse(s, out var t)) return t;

        return null;
    }


    private static readonly Regex ShiftNameTrailingTime = new Regex(@"\s*\(\s*\d{2}:\d{2}\s*-\s*\d{2}:\d{2}\s*\)\s*$", RegexOptions.Compiled);

    private static string CleanShiftName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        return ShiftNameTrailingTime.Replace(name.Trim(), "").Trim();
    }

    /*public async Task<string> ExportWeekAllAsync(DateOnly dateFrom, DateOnly dateTo, bool includeInactiveEmployees, string outputPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory);

        using var conn = _db.Create();

        // employees
        var employees = (await conn.QueryAsync<EmployeeDto>(
            new CommandDefinition(
                "dbo.usp_Employee_List",
                new { IncludeInactive = includeInactiveEmployees },
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct
            ))).ToList();

        // schedule week
        var scheduleRows = (await conn.QueryAsync<ScheduleWeekRowDto>(
            new CommandDefinition(
                "dbo.usp_Schedule_WeekAll",
                new
                {
                    DateFrom = dateFrom.ToDateTime(TimeOnly.MinValue),
                    DateTo = dateTo.ToDateTime(TimeOnly.MinValue),
                    IncludeInactiveEmployees = includeInactiveEmployees
                },
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct
            ))).ToList();

        // index by empId + workDate
        var map = scheduleRows
            .Where(x => x.EmployeeId > 0)
            .GroupBy(x => new { x.EmployeeId, Day = DateOnly.FromDateTime(x.WorkDate) })
            .ToDictionary(
                g => (g.Key.EmployeeId, g.Key.Day),
                g => g.OrderBy(x => x.StartTime ?? TimeSpan.Zero)
                      .ThenBy(x => x.ShiftId)
                      .ToList()
            );

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Lich_tuan");

        // Title
        ws.Cell(1, 1).Value = "LỊCH TUẦN";
        ws.Cell(2, 1).Value = $"Từ {dateFrom:dd/MM/yyyy} đến {dateTo:dd/MM/yyyy}";
        ws.Range(1, 1, 1, 9).Merge().Style.Font.SetBold().Font.SetFontSize(16);
        ws.Range(2, 1, 2, 9).Merge().Style.Font.SetBold().Font.SetFontSize(11);
        ws.Row(1).Height = 24;

        // Header
        const int headerRow = 4;
        ws.Cell(headerRow, 1).Value = "Mã NV";
        ws.Cell(headerRow, 2).Value = "Họ tên";

        var dayNames = new[] { "T2", "T3", "T4", "T5", "T6", "T7", "CN" };
        for (int i = 0; i < 7; i++)
        {
            var d = dateFrom.AddDays(i);
            ws.Cell(headerRow, 3 + i).Value = $"{dayNames[i]}\n{d:dd/MM}";
            ws.Cell(headerRow, 3 + i).Style.Alignment.WrapText = true;
        }

        var headerRange = ws.Range(headerRow, 1, headerRow, 9);
        headerRange.Style.Font.SetBold();
        headerRange.Style.Fill.SetBackgroundColor(XLColor.FromHtml("#E7F2F3"));
        headerRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        headerRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        headerRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        // Data
        int r = headerRow + 1;
        foreach (var emp in employees.OrderBy(e => e.EmployeeCode, StringComparer.OrdinalIgnoreCase))
        {
            ws.Cell(r, 1).Value = emp.EmployeeCode;
            ws.Cell(r, 2).Value = emp.FullName;

            for (int i = 0; i < 7; i++)
            {
                var day = dateFrom.AddDays(i);
                if (!map.TryGetValue((emp.EmployeeId, day), out var arr) || arr.Count == 0)
                {
                    ws.Cell(r, 3 + i).Value = "Nghỉ";
                    ws.Cell(r, 3 + i).Style.Font.FontColor = XLColor.FromHtml("#64748B");
                }
                else
                {
                    var blocks = arr.Select(x =>
                    {
                        var code = string.IsNullOrWhiteSpace(x.ShiftCode) ? "" : x.ShiftCode.Trim();
                        var name = CleanShiftName(x.ShiftName);
                        var time = (x.StartTime.HasValue && x.EndTime.HasValue)
                            ? $"{x.StartTime.Value:hh\\:mm}-{x.EndTime.Value:hh\\:mm}"
                            : "";

                        var line1 = string.IsNullOrWhiteSpace(code)
                            ? name
                            : (string.IsNullOrWhiteSpace(name) ? code : $"{code} • {name}");

                        var lines = new List<string>();
                        if (!string.IsNullOrWhiteSpace(line1)) lines.Add(line1);
                        if (!string.IsNullOrWhiteSpace(time)) lines.Add(time);
                        if (!string.IsNullOrWhiteSpace(x.Note)) lines.Add($"Ghi chú: {x.Note}");

                        return string.Join("\n", lines);
                    }).ToList();

                    ws.Cell(r, 3 + i).Value = string.Join("\n\n", blocks);
                    ws.Cell(r, 3 + i).Style.Alignment.WrapText = true;
                    ws.Cell(r, 3 + i).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
                }
            }

            r++;
        }

        // Formatting
        ws.Columns(1, 1).Width = 10;
        ws.Columns(2, 2).Width = 24;
        ws.Columns(3, 9).Width = 22;

        var used = ws.RangeUsed();
        if (used != null)
        {
            used.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            used.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        }

        ws.SheetView.FreezeRows(headerRow);

        wb.SaveAs(outputPath);
        _logger.LogInformation("Exported weekly schedule excel: {Path}", outputPath);
        return outputPath;
    }*/


    public async Task<string> ExportWeekAllAsync(
    DateOnly dateFrom,
    DateOnly dateTo,
    bool includeInactiveEmployees,
    string outputPath,
    CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory);

        using var conn = _db.Create();

        // employees
        var employees = (await conn.QueryAsync<EmployeeDto>(
            new CommandDefinition(
                "dbo.usp_Employee_List",
                new { IncludeInactive = includeInactiveEmployees },
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct
            ))).ToList();

        // schedule week
        var scheduleRows = (await conn.QueryAsync<ScheduleWeekRowDto>(
            new CommandDefinition(
                "dbo.usp_Schedule_WeekAll",
                new
                {
                    DateFrom = dateFrom.ToDateTime(TimeOnly.MinValue),
                    DateTo = dateTo.ToDateTime(TimeOnly.MinValue),
                    IncludeInactiveEmployees = includeInactiveEmployees
                },
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct
            ))).ToList();

        // index by empId + workDate
        var map = scheduleRows
            .Where(x => x.EmployeeId > 0)
            .GroupBy(x => new { x.EmployeeId, Day = DateOnly.FromDateTime(x.WorkDate) })
            .ToDictionary(
                g => (g.Key.EmployeeId, g.Key.Day),
                g => g.OrderBy(x => ParseTime(x.StartTime) ?? TimeSpan.Zero)
                      .ThenBy(x => x.ShiftId)
                      .ToList()
            );

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Lich_tuan");

        // Title
        ws.Cell(1, 1).Value = "LỊCH TUẦN";
        ws.Cell(2, 1).Value = $"Từ {dateFrom:dd/MM/yyyy} đến {dateTo:dd/MM/yyyy}";
        ws.Range(1, 1, 1, 9).Merge().Style.Font.SetBold().Font.SetFontSize(16);
        ws.Range(2, 1, 2, 9).Merge().Style.Font.SetBold().Font.SetFontSize(11);
        ws.Row(1).Height = 24;

        // Header
        const int headerRow = 4;
        ws.Cell(headerRow, 1).Value = "Mã NV";
        ws.Cell(headerRow, 2).Value = "Họ tên";

        var dayNames = new[] { "T2", "T3", "T4", "T5", "T6", "T7", "CN" };
        for (int i = 0; i < 7; i++)
        {
            var d = dateFrom.AddDays(i);
            ws.Cell(headerRow, 3 + i).Value = $"{dayNames[i]}\n{d:dd/MM}";
            ws.Cell(headerRow, 3 + i).Style.Alignment.WrapText = true;
        }

        var headerRange = ws.Range(headerRow, 1, headerRow, 9);
        headerRange.Style.Font.SetBold();
        headerRange.Style.Fill.SetBackgroundColor(XLColor.FromHtml("#E7F2F3"));
        headerRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        headerRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        headerRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        // Data
        int r = headerRow + 1;
        foreach (var emp in employees.OrderBy(e => e.EmployeeCode, StringComparer.OrdinalIgnoreCase))
        {
            ws.Cell(r, 1).Value = emp.EmployeeCode;
            ws.Cell(r, 2).Value = emp.FullName;

            for (int i = 0; i < 7; i++)
            {
                var day = dateFrom.AddDays(i);

                if (!map.TryGetValue((emp.EmployeeId, day), out var arr) || arr.Count == 0)
                {
                    ws.Cell(r, 3 + i).Value = "Nghỉ";
                    ws.Cell(r, 3 + i).Style.Font.FontColor = XLColor.FromHtml("#64748B");
                    continue;
                }

                var blocks = arr.Select(x =>
                {
                    var code = string.IsNullOrWhiteSpace(x.ShiftCode) ? "" : x.ShiftCode.Trim();
                    var name = CleanShiftName(x.ShiftName);

                    var st = ParseTime(x.StartTime);
                    var et = ParseTime(x.EndTime);

                    var time = (st.HasValue && et.HasValue)
                        ? $"{st.Value:hh\\:mm}-{et.Value:hh\\:mm}"
                        : "";

                    var line1 = string.IsNullOrWhiteSpace(code)
                        ? name
                        : (string.IsNullOrWhiteSpace(name) ? code : $"{code} • {name}");

                    var lines = new List<string>();
                    if (!string.IsNullOrWhiteSpace(line1)) lines.Add(line1);
                    if (!string.IsNullOrWhiteSpace(time)) lines.Add(time);
                    if (!string.IsNullOrWhiteSpace(x.Note)) lines.Add($"Ghi chú: {x.Note}");

                    return string.Join("\n", lines);
                }).ToList();

                ws.Cell(r, 3 + i).Value = string.Join("\n\n", blocks);
                ws.Cell(r, 3 + i).Style.Alignment.WrapText = true;
                ws.Cell(r, 3 + i).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            }

            r++;
        }

        // Formatting
        ws.Columns(1, 1).Width = 10;
        ws.Columns(2, 2).Width = 24;
        ws.Columns(3, 9).Width = 22;

        var used = ws.RangeUsed();
        if (used != null)
        {
            used.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            used.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        }

        ws.SheetView.FreezeRows(headerRow);

        wb.SaveAs(outputPath);
        _logger.LogInformation("Exported weekly schedule excel: {Path}", outputPath);
        return outputPath;
    }
}
