namespace TimeAttendance.Api.Models;

public sealed record ApiResponse<T>(
    bool Ok,
    string Message,
    T? Data = default,
    int? ErrorCode = null);

public sealed record TokenDto(
    string KioskCode,
    long Counter,
    string Signature,
    string Url,
    int ExpiresInSeconds);

public sealed record EmployeeBriefDto(
    string EmployeeCode,
    string FullName);

public sealed record AttendanceActionRequest(
    string EmployeeCode,
    string Pin,
    string KioskCode,
    long Counter,
    string Signature,
    string? DeviceToken = null,
    string? ManagerCode = null);
