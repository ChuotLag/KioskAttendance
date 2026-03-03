using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using System.Data;

namespace TimeAttendance.WinForms.Infrastructure
{
    public interface IAttendanceRepository
    {
        Task CheckInAsync(string employeeCode, string pin, Guid deviceGuid);
        Task CheckOutAsync(string employeeCode, string pin, Guid deviceGuid);
    }

    public sealed class AttendanceRepository : IAttendanceRepository
    {
        private readonly ISqlConnectionFactory _db;

        public AttendanceRepository(ISqlConnectionFactory db)
        {
            _db = db;
        }

        public async Task CheckInAsync(string employeeCode, string pin, Guid deviceGuid)
        {
            try
            {
                using var conn = _db.Create();

                var p = new DynamicParameters();
                p.Add("@EmployeeCode", employeeCode);
                p.Add("@Pin", pin);
                p.Add("@DeviceGuid", deviceGuid);

                // Nếu proc của bạn có @OtpCounter / @UserAgent thì mở 2 dòng dưới:
                p.Add("@OtpCounter", DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
                p.Add("@UserAgent", "WinForms-WebView2");

                await conn.ExecuteAsync(
                    "att.usp_CheckIn",
                    p,
                    commandType: CommandType.StoredProcedure);
            }
            catch (SqlException ex)
            {
                throw SqlErrorMapper.ToDomainException(ex);
            }
        }

        public async Task CheckOutAsync(string employeeCode, string pin, Guid deviceGuid)
        {
            try
            {
                using var conn = _db.Create();

                var p = new DynamicParameters();
                p.Add("@EmployeeCode", employeeCode);
                p.Add("@Pin", pin);
                p.Add("@DeviceGuid", deviceGuid);

                p.Add("@OtpCounter", DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
                p.Add("@UserAgent", "WinForms-WebView2");

                await conn.ExecuteAsync(
                    "att.usp_CheckOut",
                    p,
                    commandType: CommandType.StoredProcedure);
            }
            catch (SqlException ex)
            {
                throw SqlErrorMapper.ToDomainException(ex);
            }
        }
    }
}
