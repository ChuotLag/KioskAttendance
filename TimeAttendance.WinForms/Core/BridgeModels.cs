using System.Text.Json;
using System.Text.Json.Serialization;

namespace TimeAttendance.WinForms.Core;

public sealed record CheckActionPayload(string EmployeeCode, string Pin);
public sealed record EmployeeListPayload(bool IncludeInactive);

public sealed record ShiftListPayload(bool IncludeInactive);

public sealed record EmployeeCreatePayload(
    string EmployeeCode,
    string FullName,
    string? Phone,
    decimal HourlyRate,
    string? Pin);
public sealed record EmployeeUpdatePayload(
    long EmployeeId,
/*    string EmployeeCode,*/
    string FullName,
    string? Phone,
    decimal HourlyRate,
    bool IsActive,
    string? Pin);
public sealed record EmployeeDeletePayload(long EmployeeId, bool HardDelete);

// Admin PIN (unlock protected modules)
public sealed record AdminPinVerifyPayload(string Pin);
public sealed record AdminPinChangePayload(string CurrentPin, string NewPin);
public sealed record AdminPinResetRecoveryPayload(string RecoveryCode, string NewPin);

public sealed record BridgeRequest(string Id, string Type, JsonElement Payload);

public sealed record BridgeResponse(
    string Id,
    [property: JsonPropertyName("ok")] bool IsOk,
    string Message,
    object? Data = null,
    int? ErrorCode = null)
{
    public static BridgeResponse Ok(string id, string message, object? data = null)
        => new(id, true, message, data, null);

    public static BridgeResponse Fail(string id, string message, int? errorCode = null)
        => new(id, false, message, null, errorCode);
}

public sealed record ScheduleListPayload(
    long EmployeeId,
    string DateFrom,   // "YYYY-MM-DD"
    string DateTo      // "YYYY-MM-DD"
);

// NOTE: ScheduleId is optional.
// - null/0 => insert NEW assignment (allows many shifts per day)
// - >0     => update existing assignment by ScheduleId
public sealed record ScheduleUpsertPayload(
    long EmployeeId,
    string WorkDate,   // "YYYY-MM-DD"
    int ShiftId,
    string? Note,
    long? ScheduleId = null
);

public sealed record ScheduleDeletePayload(
    long EmployeeId,
    string WorkDate    // "YYYY-MM-DD"
);

public sealed record ScheduleDeleteByIdPayload(
    long ScheduleId
);

public sealed record ScheduleWeekAllPayload(
    string DateFrom,   // "yyyy-MM-dd"
    string DateTo,     // "yyyy-MM-dd"
    bool IncludeInactiveEmployees
);
public sealed record ScheduleWeekExportExcelPayload(
    string DateFrom,   // "yyyy-MM-dd"
    string DateTo,     // "yyyy-MM-dd"
    bool IncludeInactiveEmployees
);
// Payroll
public sealed record PayrollPreviewPayload(
    string DateFrom,   // "yyyy-MM-dd"
    string DateTo,     // "yyyy-MM-dd"
    long? EmployeeId   // null = all
);

public sealed record PayrollExportExcelPayload(
    string DateFrom,   // "yyyy-MM-dd"
    string DateTo,     // "yyyy-MM-dd"
    long? EmployeeId,  // null = all
    bool SplitToSheetsByEmployee = true
);


// Pay multiplier (nhân đôi/nhân ba lương theo ngày)
public sealed record PayMultiplierListPayload(
    string DateFrom,
    string DateTo
);

public sealed record PayMultiplierUpsertPayload(
    string WorkDate,
    decimal Multiplier,
    string? Note
);

public sealed record PayMultiplierDeletePayload(
    string WorkDate
);

// Settings (manual API endpoint)
public sealed record ApiEndpointPayload(string Scheme, string Host, int Port);
public sealed record SettingsSavePayload(ApiEndpointPayload Api, bool ApplyToBaseUrl = true, bool ApplyToPublicBaseUrl = true);
public sealed record ApiTestPayload(string Scheme, string Host, int Port);
