using Dapper;

namespace TimeAttendance.Api.Infrastructure;

public interface IDeviceBindingStore
{
    /// <summary>
    /// Ensure (EmployeeCode, DeviceToken) is approved. If not approved, caller must provide managerCode.
    /// Returns (Ok=true) when device is approved (either already approved or approved in this call).
    /// </summary>
    Task<(bool Ok, bool NeedsManager, string Message)> EnsureApprovedAsync(
        string employeeCode,
        string deviceToken,
        string? deviceLabel,
        string? managerCode,
        CancellationToken ct = default);
}

public sealed class DeviceBindingStore : IDeviceBindingStore
{
    private readonly ISqlConnectionFactory _db;
    private readonly IConfiguration _cfg;

    public DeviceBindingStore(ISqlConnectionFactory db, IConfiguration cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    public async Task<(bool Ok, bool NeedsManager, string Message)> EnsureApprovedAsync(
        string employeeCode,
        string deviceToken,
        string? deviceLabel,
        string? managerCode,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(employeeCode))
            return (false, false, "Thiếu mã nhân viên");

        if (string.IsNullOrWhiteSpace(deviceToken))
            return (false, false, "Thiếu thông tin thiết bị. Vui lòng thử lại.");

        // Optional switch to disable device binding (for debugging)
        var enabled = _cfg.GetValue("DeviceBinding:Enabled", true);
        if (!enabled)
            return (true, false, "OK");

        var approvalCode = (_cfg["Manager:ApprovalCode"] ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(approvalCode))
        {
            // If manager code is not configured, fallback to allow (avoid breaking deployments).
            return (true, false, "OK");
        }

        using var conn = _db.Create();

        // Auto-migrate minimal table if missing (best-effort).
        if (_cfg.GetValue("DeviceBinding:AutoMigrate", true))
        {
            try
            {
                await conn.ExecuteAsync(new CommandDefinition(SqlCreateTableIfMissing, cancellationToken: ct));
            }
            catch
            {
                // ignore; if permissions are limited, table should be created manually.
            }
        }

        // Load existing record.
        // IMPORTANT: do NOT use a value-tuple here. With value-types, "not found" returns default values,
        // which is indistinguishable from a real row like (IsApproved=0, RevokedAt=NULL) and will cause
        // duplicate INSERTs -> PK violations.
        const string sqlGet = @"SELECT TOP 1 DeviceToken, IsApproved, RevokedAt
FROM dbo.EmployeeDevices
WHERE EmployeeCode=@EmployeeCode AND DeviceToken=@DeviceToken";

        var row = await conn.QueryFirstOrDefaultAsync<DeviceRow>(
            new CommandDefinition(sqlGet, new { EmployeeCode = employeeCode.Trim(), DeviceToken = deviceToken.Trim() }, cancellationToken: ct));

        var exists = row != null;
        var isRevoked = row?.RevokedAt != null;
        var isApproved = (row?.IsApproved ?? false) && !isRevoked;

        // Update last seen / create if missing
        if (!exists)
        {
            const string sqlInsert = @"
                IF OBJECT_ID('dbo.EmployeeDevices','U') IS NOT NULL
                BEGIN
                    INSERT INTO dbo.EmployeeDevices(EmployeeCode, DeviceToken, DeviceLabel, IsApproved)
                    VALUES (@EmployeeCode, @DeviceToken, @DeviceLabel, 0);
                END
                ";
            await conn.ExecuteAsync(new CommandDefinition(sqlInsert, new
            {
                EmployeeCode = employeeCode.Trim(),
                DeviceToken = deviceToken.Trim(),
                DeviceLabel = (deviceLabel ?? string.Empty).Trim()
            }, cancellationToken: ct));
        }
        else
        {
            const string sqlTouch = @"UPDATE dbo.EmployeeDevices
SET LastSeenAt=SYSUTCDATETIME(), DeviceLabel=COALESCE(NULLIF(@DeviceLabel,''), DeviceLabel)
WHERE EmployeeCode=@EmployeeCode AND DeviceToken=@DeviceToken";
            await conn.ExecuteAsync(new CommandDefinition(sqlTouch, new
            {
                EmployeeCode = employeeCode.Trim(),
                DeviceToken = deviceToken.Trim(),
                DeviceLabel = (deviceLabel ?? string.Empty).Trim()
            }, cancellationToken: ct));
        }

        if (isApproved)
            return (true, false, "OK");

        // If this is the FIRST device of this employee (no active approved device yet), auto-approve
        // to reduce friction for onboarding. From the 2nd device onward, require manager code.
        const string sqlApprovedCount = @"SELECT COUNT(1)
FROM dbo.EmployeeDevices
WHERE EmployeeCode=@EmployeeCode AND IsApproved=1 AND RevokedAt IS NULL";

        var approvedCount = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(sqlApprovedCount, new { EmployeeCode = employeeCode.Trim() }, cancellationToken: ct));

        if (approvedCount == 0)
        {
            const string sqlAutoApproveFirst = @"
IF OBJECT_ID('dbo.EmployeeDevices','U') IS NULL RETURN;

-- Keep only one active device per employee (revoke others, even if they are not approved).
UPDATE dbo.EmployeeDevices
SET RevokedAt = SYSUTCDATETIME(), RevokedBy = 'auto_first_device'
WHERE EmployeeCode=@EmployeeCode AND RevokedAt IS NULL AND DeviceToken <> @DeviceToken;

UPDATE dbo.EmployeeDevices
SET IsApproved=1,
    ApprovedAt=COALESCE(ApprovedAt, SYSUTCDATETIME()),
    ApprovedBy=COALESCE(NULLIF(ApprovedBy,''), 'auto_first_device'),
    RevokedAt=NULL,
    RevokedBy=NULL,
    LastSeenAt=SYSUTCDATETIME(),
    DeviceLabel=COALESCE(NULLIF(@DeviceLabel,''), DeviceLabel)
WHERE EmployeeCode=@EmployeeCode AND DeviceToken=@DeviceToken;
";

            await conn.ExecuteAsync(new CommandDefinition(sqlAutoApproveFirst, new
            {
                EmployeeCode = employeeCode.Trim(),
                DeviceToken = deviceToken.Trim(),
                DeviceLabel = (deviceLabel ?? string.Empty).Trim()
            }, cancellationToken: ct));

            return (true, false, "OK");
        }

        // Not approved (or revoked). Require manager approval code.
        if (string.IsNullOrWhiteSpace(managerCode) || !string.Equals(managerCode.Trim(), approvalCode, StringComparison.Ordinal))
        {
            return (false, true, "Thiết bị mới hoặc chưa được duyệt. Vui lòng nhập mã quản lý để duyệt thiết bị.");
        }

        // Approve this device, revoke previous approved devices (single active device)
        const string sqlApprove = @"
IF OBJECT_ID('dbo.EmployeeDevices','U') IS NULL RETURN;

UPDATE dbo.EmployeeDevices
SET RevokedAt = SYSUTCDATETIME(), RevokedBy = @ApprovedBy
WHERE EmployeeCode=@EmployeeCode AND IsApproved=1 AND RevokedAt IS NULL AND DeviceToken <> @DeviceToken;

UPDATE dbo.EmployeeDevices
SET IsApproved=1, ApprovedAt=SYSUTCDATETIME(), ApprovedBy=@ApprovedBy,
    RevokedAt=NULL, RevokedBy=NULL,
    LastSeenAt=SYSUTCDATETIME(),
    DeviceLabel=COALESCE(NULLIF(@DeviceLabel,''), DeviceLabel)
WHERE EmployeeCode=@EmployeeCode AND DeviceToken=@DeviceToken;
";

        await conn.ExecuteAsync(new CommandDefinition(sqlApprove, new
        {
            EmployeeCode = employeeCode.Trim(),
            DeviceToken = deviceToken.Trim(),
            DeviceLabel = (deviceLabel ?? string.Empty).Trim(),
            ApprovedBy = "manager_code"
        }, cancellationToken: ct));

        // Approved successfully. We allow the current request to continue.
        return (true, false, "OK");
    }

    // NOTE: use a reference-type for Dapper mapping so "not found" returns null.
    private sealed class DeviceRow
    {
        public string DeviceToken { get; set; } = string.Empty;
        public bool IsApproved { get; set; }
        public DateTime? RevokedAt { get; set; }
    }

    private const string SqlCreateTableIfMissing = @"
IF OBJECT_ID('dbo.EmployeeDevices','U') IS NULL
BEGIN
    CREATE TABLE dbo.EmployeeDevices(
        EmployeeCode NVARCHAR(50) NOT NULL,
        DeviceToken NVARCHAR(64) NOT NULL,
        DeviceLabel NVARCHAR(128) NULL,
        FirstSeenAt DATETIME2 NOT NULL CONSTRAINT DF_EmployeeDevices_FirstSeenAt DEFAULT SYSUTCDATETIME(),
        LastSeenAt  DATETIME2 NOT NULL CONSTRAINT DF_EmployeeDevices_LastSeenAt  DEFAULT SYSUTCDATETIME(),
        IsApproved  BIT NOT NULL CONSTRAINT DF_EmployeeDevices_IsApproved DEFAULT 0,
        ApprovedAt  DATETIME2 NULL,
        ApprovedBy  NVARCHAR(50) NULL,
        RevokedAt   DATETIME2 NULL,
        RevokedBy   NVARCHAR(50) NULL,
        CONSTRAINT PK_EmployeeDevices PRIMARY KEY(EmployeeCode, DeviceToken)
    );
    CREATE INDEX IX_EmployeeDevices_Employee_Approved ON dbo.EmployeeDevices(EmployeeCode, IsApproved, RevokedAt);
END
";
}
