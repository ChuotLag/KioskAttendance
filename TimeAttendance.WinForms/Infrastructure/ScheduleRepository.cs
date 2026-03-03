using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using Dapper;


namespace TimeAttendance.WinForms.Infrastructure
{
    public sealed class ScheduleRepository
    {
        private readonly string _cs;
        public ScheduleRepository(string connectionString) => _cs = connectionString;

        public async Task<List<object>> ListAsync(long employeeId, DateOnly from, DateOnly to, CancellationToken ct)
        {
            var list = new List<object>();

            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);

            await using var cmd = new SqlCommand("dbo.usp_Schedule_List", conn);
            cmd.CommandType = CommandType.StoredProcedure;

            cmd.Parameters.Add("@DateFrom", SqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
            cmd.Parameters.Add("@DateTo", SqlDbType.Date).Value = to.ToDateTime(TimeOnly.MinValue);
            cmd.Parameters.Add("@EmployeeId", SqlDbType.BigInt).Value = employeeId;

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add(new
                {
                    ScheduleId = Convert.ToInt64(r["ScheduleId"]),
                    EmployeeId = Convert.ToInt64(r["EmployeeId"]),
                    WorkDate = r.GetDateTime(r.GetOrdinal("WorkDate")).ToString("yyyy-MM-dd"),
                    ShiftId = r.GetInt32(r.GetOrdinal("ShiftId")),
                    Note = r.IsDBNull(r.GetOrdinal("Note")) ? null : r.GetString(r.GetOrdinal("Note")),
                    ShiftCode = r.GetString(r.GetOrdinal("ShiftCode")),
                    ShiftName = r.GetString(r.GetOrdinal("ShiftName")),
                    StartTime = r.GetTimeSpan(r.GetOrdinal("StartTime")).ToString(@"hh\:mm"),
                    EndTime = r.GetTimeSpan(r.GetOrdinal("EndTime")).ToString(@"hh\:mm"),
                });
            }

            return list;
        }

        public async Task<object?> UpsertAsync(long employeeId, DateOnly workDate, int shiftId, string? note, long? scheduleId, CancellationToken ct)
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);

            await using var cmd = new SqlCommand("dbo.usp_Schedule_Upsert", conn);
            cmd.CommandType = CommandType.StoredProcedure;

            cmd.Parameters.Add("@EmployeeId", SqlDbType.BigInt).Value = employeeId;
            cmd.Parameters.Add("@WorkDate", SqlDbType.Date).Value = workDate.ToDateTime(TimeOnly.MinValue);
            cmd.Parameters.Add("@ShiftId", SqlDbType.Int).Value = shiftId;
            cmd.Parameters.Add("@Note", SqlDbType.NVarChar, 200).Value = (object?)note ?? DBNull.Value;
            cmd.Parameters.Add("@ScheduleId", SqlDbType.BigInt).Value = (scheduleId.HasValue && scheduleId.Value > 0)
                ? scheduleId.Value
                : DBNull.Value;

            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;

            return new
            {
                ScheduleId = r.GetInt64(r.GetOrdinal("ScheduleId")),
                EmployeeId = r.GetInt64(r.GetOrdinal("EmployeeId")),
                WorkDate = r.GetDateTime(r.GetOrdinal("WorkDate")).ToString("yyyy-MM-dd"),
                ShiftId = r.GetInt32(r.GetOrdinal("ShiftId")),
                Note = r.IsDBNull(r.GetOrdinal("Note")) ? null : r.GetString(r.GetOrdinal("Note")),
            };
        }

        public async Task DeleteAsync(long employeeId, DateOnly workDate, CancellationToken ct)
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);

            await using var cmd = new SqlCommand("dbo.usp_Schedule_Delete", conn);
            cmd.CommandType = CommandType.StoredProcedure;

            cmd.Parameters.Add("@EmployeeId", SqlDbType.BigInt).Value = employeeId;
            cmd.Parameters.Add("@WorkDate", SqlDbType.Date).Value = workDate.ToDateTime(TimeOnly.MinValue);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task DeleteByIdAsync(long scheduleId, CancellationToken ct)
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);

            await using var cmd = new SqlCommand("dbo.usp_Schedule_DeleteById", conn);
            cmd.CommandType = CommandType.StoredProcedure;

            cmd.Parameters.Add("@ScheduleId", SqlDbType.BigInt).Value = scheduleId;

            await cmd.ExecuteNonQueryAsync(ct);
        }


        public async Task<IEnumerable<dynamic>> ListWeekAllAsync(DateOnly dateFrom, DateOnly dateTo, bool includeInactiveEmployees, CancellationToken ct)
        {
            using var conn = new SqlConnection(_cs);
            var p = new DynamicParameters();
            p.Add("@DateFrom", dateFrom.ToDateTime(TimeOnly.MinValue));
            p.Add("@DateTo", dateTo.ToDateTime(TimeOnly.MinValue));
            p.Add("@IncludeInactiveEmployees", includeInactiveEmployees);

            var cmd = new CommandDefinition(
                "dbo.usp_Schedule_WeekAll",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct
            );

            return await conn.QueryAsync(cmd);
        }

    }

}
