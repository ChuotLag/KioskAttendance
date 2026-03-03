using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace TimeAttendance.WinForms.Infrastructure;

public interface IAdminPinRepository
{
    Task EnsureInitializedAsync(CancellationToken ct = default);
    Task<bool> HasPinAsync(CancellationToken ct = default);
    Task<bool> VerifyAsync(string pin, CancellationToken ct = default);
    Task ChangeAsync(string currentPin, string newPin, CancellationToken ct = default);
    Task ResetToDefaultAsync(CancellationToken ct = default);
    Task<(bool Ok, string Message)> ResetWithRecoveryCodeAsync(string recoveryCode, string newPin, CancellationToken ct = default);
    string DefaultPin { get; }
}

/// <summary>
/// Single Admin PIN used to unlock protected modules in the WinForms Web UI.
/// Stored in DB as (salt + SHA256).
/// </summary>
public sealed class AdminPinRepository : IAdminPinRepository
{
    private readonly ISqlConnectionFactory _db;
    private readonly IConfiguration _cfg;

    public string DefaultPin { get; }

    public AdminPinRepository(ISqlConnectionFactory db, IConfiguration cfg)
    {
        _db = db;
        _cfg = cfg;
        DefaultPin = (cfg["AdminPin:DefaultPin"] ?? "260302").Trim();
        if (string.IsNullOrWhiteSpace(DefaultPin)) DefaultPin = "260302";
    }

    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        using var conn = _db.Create();

        // Best-effort auto-migrate
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(SqlCreateTableIfMissing, cancellationToken: ct));
        }
        catch
        {
            // ignore - if DB permissions are limited, admin can create table manually
        }

        // Ensure a row exists
        var exists = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition("SELECT COUNT(1) FROM dbo.AppAdminPin WHERE Id=1", cancellationToken: ct));

        if (exists <= 0)
        {
            await SetPinInternalAsync(conn, DefaultPin, ct);
        }
    }

    public async Task<bool> HasPinAsync(CancellationToken ct = default)
    {
        using var conn = _db.Create();
        try
        {
            var n = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition("SELECT COUNT(1) FROM dbo.AppAdminPin WHERE Id=1", cancellationToken: ct));
            return n > 0;
        }
        catch (SqlException)
        {
            return false;
        }
    }

    public async Task<bool> VerifyAsync(string pin, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pin)) return false;

        using var conn = _db.Create();

        // Ensure exists (for first run)
        await EnsureInitializedAsync(ct);

        try
        {
            var row = await conn.QueryFirstOrDefaultAsync<PinRow>(
                new CommandDefinition(
                    "SELECT TOP 1 PinHash, PinSalt FROM dbo.AppAdminPin WHERE Id=1",
                    cancellationToken: ct));

            if (row?.PinHash == null || row.PinSalt == null) return false;

            var computed = ComputeHash(row.PinSalt, pin.Trim());
            return CryptographicOperations.FixedTimeEquals(row.PinHash, computed);
        }
        catch (SqlException)
        {
            return false;
        }
    }

    public async Task ChangeAsync(string currentPin, string newPin, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newPin) || newPin.Trim().Length < 4)
            throw new InvalidOperationException("PIN mới phải có ít nhất 4 ký tự.");

        var ok = await VerifyAsync(currentPin, ct);
        if (!ok) throw new InvalidOperationException("PIN hiện tại không đúng.");

        using var conn = _db.Create();
        await EnsureInitializedAsync(ct);
        await SetPinInternalAsync(conn, newPin.Trim(), ct);
    }

    public async Task ResetToDefaultAsync(CancellationToken ct = default)
    {
        using var conn = _db.Create();
        await EnsureInitializedAsync(ct);
        await SetPinInternalAsync(conn, DefaultPin, ct);
    }

    public async Task<(bool Ok, string Message)> ResetWithRecoveryCodeAsync(string recoveryCode, string newPin, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newPin) || newPin.Trim().Length < 4)
            return (false, "PIN mới phải có ít nhất 4 ký tự.");

        var rc = (_cfg["AdminPin:RecoveryCode"] ?? _cfg["Manager:ApprovalCode"] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(rc))
            return (false, "Chưa cấu hình mã khôi phục (AdminPin:RecoveryCode).");

        if (!string.Equals((recoveryCode ?? "").Trim(), rc, StringComparison.Ordinal))
            return (false, "Mã khôi phục không đúng.");

        using var conn = _db.Create();
        await EnsureInitializedAsync(ct);
        await SetPinInternalAsync(conn, newPin.Trim(), ct);
        return (true, "Đã đặt lại PIN.");
    }

    private static byte[] ComputeHash(byte[] salt, string pin)
    {
        var pinBytes = Encoding.Unicode.GetBytes(pin);
        var buf = new byte[salt.Length + pinBytes.Length];
        Buffer.BlockCopy(salt, 0, buf, 0, salt.Length);
        Buffer.BlockCopy(pinBytes, 0, buf, salt.Length, pinBytes.Length);
        return SHA256.HashData(buf);
    }

    private static async Task SetPinInternalAsync(System.Data.IDbConnection conn, string pin, CancellationToken ct)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = ComputeHash(salt, pin);

        const string sql = @"
MERGE dbo.AppAdminPin AS t
USING (SELECT CAST(1 AS INT) AS Id) AS s
ON t.Id = s.Id
WHEN MATCHED THEN
  UPDATE SET PinHash=@PinHash, PinSalt=@PinSalt, UpdatedAt=SYSDATETIME()
WHEN NOT MATCHED THEN
  INSERT (Id, PinHash, PinSalt, UpdatedAt)
  VALUES (1, @PinHash, @PinSalt, SYSDATETIME());";

        await conn.ExecuteAsync(new CommandDefinition(sql, new { PinHash = hash, PinSalt = salt }, cancellationToken: ct));
    }

    private sealed class PinRow
    {
        public byte[] PinHash { get; set; } = Array.Empty<byte>();
        public byte[] PinSalt { get; set; } = Array.Empty<byte>();
    }

    private const string SqlCreateTableIfMissing = @"
IF OBJECT_ID('dbo.AppAdminPin','U') IS NULL
BEGIN
    CREATE TABLE dbo.AppAdminPin(
        Id INT NOT NULL CONSTRAINT PK_AppAdminPin PRIMARY KEY,
        PinHash VARBINARY(32) NOT NULL,
        PinSalt VARBINARY(16) NOT NULL,
        UpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_AppAdminPin_UpdatedAt DEFAULT SYSDATETIME()
    );
END
";
}
