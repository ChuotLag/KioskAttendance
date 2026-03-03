using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;


namespace TimeAttendance.WinForms.Infrastructure
{
    public sealed class ShiftRepository
    {
        private readonly string _cs;
        public ShiftRepository(string connectionString) => _cs = connectionString;

        public async Task<List<object>> ListAsync(bool includeInactive, CancellationToken ct)
        {
            var list = new List<object>();

            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);

            await using var cmd = new SqlCommand("dbo.usp_Shift_List", conn);
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.Parameters.Add("@IncludeInactive", SqlDbType.Bit).Value = includeInactive;

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add(new
                {
                    ShiftId = r.GetInt32(r.GetOrdinal("ShiftId")),
                    ShiftCode = r.GetString(r.GetOrdinal("ShiftCode")),
                    ShiftName = r.GetString(r.GetOrdinal("ShiftName")),
                    StartTime = r.GetTimeSpan(r.GetOrdinal("StartTime")).ToString(@"hh\:mm"),
                    EndTime = r.GetTimeSpan(r.GetOrdinal("EndTime")).ToString(@"hh\:mm"),
                    IsActive = r.GetBoolean(r.GetOrdinal("IsActive"))
                });
            }

            return list;
        }
    }


}
