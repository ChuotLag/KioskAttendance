using Microsoft.Data.SqlClient;

namespace TimeAttendance.Api.Infrastructure;

public static class SqlErrorMapper
{
    public static (int Code, string Message) Map(SqlException ex)
        => ex.Number switch
        {
            52001 => (ex.Number, "PIN không hợp lệ."),
            52002 => (ex.Number, "Không tìm thấy nhân viên (hoặc đã nghỉ)."),
            52003 => (ex.Number, "Nhân viên chưa được set PIN."),
            52004 => (ex.Number, "Sai PIN."),
            52005 => (ex.Number, "Thiết bị kiosk không hợp lệ."),
            52006 => (ex.Number, "No scheduled shift found for this employee (today/yesterday)."),
            52007 => (ex.Number, "Bạn đã check-in ca này rồi."),
            53001 => (ex.Number, "PIN không hợp lệ."),
            53002 => (ex.Number, "Không tìm thấy nhân viên (hoặc đã nghỉ)."),
            53003 => (ex.Number, "Nhân viên chưa được set PIN."),
            53004 => (ex.Number, "Sai PIN."),
            53005 => (ex.Number, "Thiết bị kiosk không hợp lệ."),
            53006 => (ex.Number, "No scheduled shift found for this employee (today/yesterday)."),
            53007 => (ex.Number, "Không có ca đang mở để check-out."),
            53008 => (ex.Number, "Giờ check-out phải sau check-in."),
            _ => (ex.Number, ex.Message)
        };
}
