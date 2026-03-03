using Dapper;
using Microsoft.Data.SqlClient;
using System.Data;
using TimeAttendance.WinForms.Core;

namespace TimeAttendance.WinForms.Infrastructure;

public interface IPayrollRepository
{
    Task<List<PayrollPreviewDto>> GetPreviewAsync(DateOnly dateFrom, DateOnly dateTo, long? employeeId, CancellationToken ct);
}

/// <summary>
/// Queries payroll preview from view pay.vw_PayrollPreview.
/// Note: View is expected to be updated by sql_Attendance_add_late_penalty.sql
/// (LateMinutes, PenaltyAmount, NetPay...)
/// </summary>
public sealed class PayrollRepository : IPayrollRepository
{
    private readonly ISqlConnectionFactory _db;

    public PayrollRepository(ISqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<List<PayrollPreviewDto>> GetPreviewAsync(DateOnly dateFrom, DateOnly dateTo, long? employeeId, CancellationToken ct)
    {
        try
        {
            using var conn = _db.Create();

            // Dapper doesn't pass CancellationToken for QueryAsync in older versions for IDbConnection,
            // so we keep it simple.
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
ORDER BY WorkDate ASC, EmployeeCode ASC;";

            var rows = await conn.QueryAsync<PayrollPreviewDto>(
                sql,
                new
                {
                    DateFrom = dateFrom.ToDateTime(TimeOnly.MinValue),
                    DateTo = dateTo.ToDateTime(TimeOnly.MinValue),
                    EmployeeId = employeeId
                },
                commandType: CommandType.Text
            );

            return rows.ToList();
        }
        catch (SqlException ex)
        {
            throw SqlErrorMapper.ToDomainException(ex);
        }
    }
}
