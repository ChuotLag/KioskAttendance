using Dapper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TimeAttendance.WinForms.Core;


namespace TimeAttendance.WinForms.Infrastructure
{
    // DTO trả về cho Dashboard
    /*    public sealed record DashboardDto(int CheckedIn, int Working, int NotCheckedIn, int TotalMinutes);

        // DTO cho Recent Activity
        public sealed record ActivityDto(
            string EmployeeCode,
            string FullName,
            int EventType,
            DateTime EventTime,
            string DeviceCode);*/
    public interface IDashboardRepository
    {
        Task<DashboardDto> GetDashboardAsync(DateOnly date);
        Task<List<ActivityDto>> GetRecentActivityAsync(DateOnly date, int top);
    }
    public sealed class DashboardRepository : IDashboardRepository
    {
        private readonly ISqlConnectionFactory _db;

        public DashboardRepository(ISqlConnectionFactory db)
        {
            _db = db;
        }

        public async Task<DashboardDto> GetDashboardAsync(DateOnly date)
        {
            const string sql = @"
                DECLARE @d date = @Date;

                SELECT
                  CheckedIn = (
                    SELECT COUNT(*)
                    FROM att.AttendanceRecords ar
                    WHERE ar.WorkDate = @d
                      AND ar.CheckInTime IS NOT NULL
                  ),
                  Working = (
                    SELECT COUNT(*)
                    FROM att.AttendanceRecords ar
                    WHERE ar.WorkDate = @d
                      AND ar.CheckInTime IS NOT NULL
                      AND ar.CheckOutTime IS NULL
                  ),
                  NotCheckedIn = (
                    (SELECT COUNT(*) FROM dbo.Employees e WHERE e.IsActive = 1)
                    - (SELECT COUNT(DISTINCT ar.EmployeeId)
                       FROM att.AttendanceRecords ar
                       WHERE ar.WorkDate = @d
                         AND ar.CheckInTime IS NOT NULL)
                  ),
                  TotalMinutes = (
                    SELECT ISNULL(SUM(ar.MinutesWorked), 0)
                    FROM att.AttendanceRecords ar
                    WHERE ar.WorkDate = @d
                      AND ar.MinutesWorked IS NOT NULL
                  );
                ";

            using var conn = _db.Create();
            var d = date.ToDateTime(TimeOnly.MinValue);
            return await conn.QueryFirstAsync<DashboardDto>(sql, new { Date = d });
        }

        public async Task<List<ActivityDto>> GetRecentActivityAsync(DateOnly date, int top)
        {
            // Lọc chỉ các sự kiện trong "ngày" được truyền vào (00:00 -> trước 00:00 ngày kế tiếp)
            // Không dùng DECLARE @start/@end để tránh lỗi "already been declared" khi query bị ghép batch ở nơi khác.
            const string sql = @"
            SELECT TOP (@Top)
                e.EmployeeCode,
                e.FullName,
                CAST(a.EventType AS int) AS EventType,
                a.EventTime,
                k.DeviceCode
            FROM att.AttendanceEvents a
            JOIN dbo.Employees e ON e.EmployeeId = a.EmployeeId
            JOIN dbo.KioskDevices k ON k.KioskDeviceId = a.KioskDeviceId
            WHERE a.EventTime >= @StartTime
              AND a.EventTime <  @EndTime
            ORDER BY a.EventTime DESC;
            ";

            using var conn = _db.Create();
            var start = date.ToDateTime(TimeOnly.MinValue);
            var end = start.AddDays(1);

            var rows = await conn.QueryAsync<ActivityDto>(sql, new
            {
                Top = top,
                StartTime = start,
                EndTime = end
            });

            return rows.ToList();
        }

    }

}
