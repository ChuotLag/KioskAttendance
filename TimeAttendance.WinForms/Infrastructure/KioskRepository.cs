using Dapper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TimeAttendance.WinForms.Infrastructure
{
    public interface IKioskRepository
    {
        Task EnsureKioskAsync(Guid deviceGuid, string deviceCode, string deviceName);
    }
    public sealed class KioskRepository : IKioskRepository
    {
        private readonly ISqlConnectionFactory _db;
        public KioskRepository(ISqlConnectionFactory db) => _db = db;

        public async Task EnsureKioskAsync(Guid deviceGuid, string deviceCode, string deviceName)
        {
            const string sql = @"
                IF EXISTS (SELECT 1 FROM dbo.KioskDevices WHERE DeviceGuid = @DeviceGuid)
                BEGIN
                    UPDATE dbo.KioskDevices
                    SET DeviceCode = @DeviceCode,
                        DeviceName = @DeviceName,
                        IsActive = 1,
                        LastSeenAt = COALESCE(LastSeenAt, SYSDATETIME())
                    WHERE DeviceGuid = @DeviceGuid;
                END
                ELSE IF EXISTS (SELECT 1 FROM dbo.KioskDevices WHERE DeviceCode = @DeviceCode)
                BEGIN
                    -- Nếu đã tồn tại theo DeviceCode, đồng bộ DeviceGuid theo cấu hình hiện tại.
                    UPDATE dbo.KioskDevices
                    SET DeviceGuid = @DeviceGuid,
                        DeviceName = @DeviceName,
                        IsActive = 1,
                        LastSeenAt = COALESCE(LastSeenAt, SYSDATETIME())
                    WHERE DeviceCode = @DeviceCode;
                END
                ELSE
                BEGIN
                    INSERT INTO dbo.KioskDevices(DeviceGuid, DeviceCode, DeviceName, SharedSecret)
                    VALUES (@DeviceGuid, @DeviceCode, @DeviceName, CRYPT_GEN_RANDOM(64));
                END
                ";
            using var conn = _db.Create();
            await conn.ExecuteAsync(sql, new
            {
                DeviceGuid = deviceGuid,
                DeviceCode = deviceCode,
                DeviceName = deviceName
            });
        }
    }
}
