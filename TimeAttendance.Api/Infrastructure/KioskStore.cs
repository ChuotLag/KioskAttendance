using System.Security.Cryptography;
using Dapper;
using System.Collections.Concurrent;

namespace TimeAttendance.Api.Infrastructure;

public sealed record KioskInfo(
    string DeviceCode,
    Guid DeviceGuid,
    byte[] SharedSecret);

public interface IKioskStore
{
    /// <summary>
    /// Get kiosk by deviceCode. If not exists, create it (requires deviceName).
    /// </summary>
    Task<KioskInfo> GetOrCreateAsync(string deviceCode, string deviceName, CancellationToken ct = default);
}

public sealed class KioskStore : IKioskStore
{
    private readonly ISqlConnectionFactory _db;
    private readonly ConcurrentDictionary<string, Lazy<Task<KioskInfo>>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public KioskStore(ISqlConnectionFactory db)
    {
        _db = db;
    }

    public Task<KioskInfo> GetOrCreateAsync(string deviceCode, string deviceName, CancellationToken ct = default)
    {
        var lazy = _cache.GetOrAdd(deviceCode, dc => new Lazy<Task<KioskInfo>>(() => LoadOrCreateAsync(dc, deviceName, ct)));
        return lazy.Value;
    }

    private async Task<KioskInfo> LoadOrCreateAsync(string deviceCode, string deviceName, CancellationToken ct)
    {
        using var conn = _db.Create();

        // Try load
        var row = await conn.QueryFirstOrDefaultAsync<(Guid DeviceGuid, byte[] SharedSecret)>(
            new CommandDefinition(
                "SELECT TOP 1 DeviceGuid, SharedSecret FROM dbo.KioskDevices WHERE DeviceCode=@DeviceCode AND IsActive=1",
                new { DeviceCode = deviceCode },
                cancellationToken: ct));

        if (row.DeviceGuid != Guid.Empty && row.SharedSecret is { Length: > 0 })
            return new KioskInfo(deviceCode, row.DeviceGuid, row.SharedSecret);

        // Create (idempotent-ish). If another process creates concurrently, insert may fail; we re-load.
        var deviceGuid = Guid.NewGuid();
        var secret = RandomNumberGenerator.GetBytes(64);

        try
        {
            const string sql = @"
IF NOT EXISTS (SELECT 1 FROM dbo.KioskDevices WHERE DeviceCode=@DeviceCode)
BEGIN
    INSERT INTO dbo.KioskDevices(DeviceGuid, DeviceCode, DeviceName, SharedSecret)
    VALUES (@DeviceGuid, @DeviceCode, @DeviceName, @SharedSecret);
END
";
            await conn.ExecuteAsync(new CommandDefinition(sql, new
            {
                DeviceGuid = deviceGuid,
                DeviceCode = deviceCode,
                DeviceName = deviceName,
                SharedSecret = secret
            }, cancellationToken: ct));
        }
        catch
        {
            // ignore and re-load
        }

        row = await conn.QueryFirstOrDefaultAsync<(Guid DeviceGuid, byte[] SharedSecret)>(
            new CommandDefinition(
                "SELECT TOP 1 DeviceGuid, SharedSecret FROM dbo.KioskDevices WHERE DeviceCode=@DeviceCode AND IsActive=1",
                new { DeviceCode = deviceCode },
                cancellationToken: ct));

        if (row.DeviceGuid == Guid.Empty || row.SharedSecret is null || row.SharedSecret.Length == 0)
            throw new InvalidOperationException($"Kiosk '{deviceCode}' not found/active. Please seed dbo.KioskDevices.");

        return new KioskInfo(deviceCode, row.DeviceGuid, row.SharedSecret);
    }
}
