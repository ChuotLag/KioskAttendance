using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace TimeAttendance.WinForms.Core
{
        public sealed class DashboardDto
        {
            public int CheckedIn { get; set; }
            public int Working { get; set; }
            public int NotCheckedIn { get; set; }
            public int TotalMinutes { get; set; }
        }

        // Recent activity row
        public sealed class ActivityDto
        {
            public string EmployeeCode { get; set; } = "";
            public string FullName { get; set; } = "";
            public int EventType { get; set; }         // dùng int cho dễ xử lý UI
            public DateTime EventTime { get; set; }
            public string DeviceCode { get; set; } = "";
        }

    /*      public sealed class KioskTokenDto
          {
              public string Url { get; set; } = "";

              public string C { get; set; } = "";   // hoặc Token/ChallengeCode...
              public string Sig { get; set; } = "";
              public string Code => C; // alias để code hiện tại chạy được
              public int ExpiresInSeconds { get; set; }
          }*/

    public sealed class KioskTokenDto
    {
        // kiểu cũ API trả
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        // kiểu mới (nếu API trả)
        [JsonPropertyName("c")]
        public string? C { get; set; }

        [JsonPropertyName("s")]
        public string? S { get; set; }

        // phòng khi API đặt tên khác
        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("sig")]
        public string? Sig { get; set; }

        [JsonPropertyName("expiresInSeconds")]
        public int ExpiresInSeconds { get; set; }

        // dùng thống nhất
        [JsonIgnore] public string TokenC => !string.IsNullOrWhiteSpace(C) ? C! : (Code ?? "");
        [JsonIgnore] public string TokenSig => !string.IsNullOrWhiteSpace(S) ? S! : (Sig ?? "");
    }

    public sealed class EmployeeDto
        {
            public long EmployeeId { get; set; }
            public string EmployeeCode { get; set; } = "";
            public string FullName { get; set; } = "";
            public string? Phone { get; set; }
            public decimal HourlyRate { get; set; }
            public bool IsActive { get; set; }
            public DateTime? PinChangedAt { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        // Payroll preview row (from pay.vw_PayrollPreview)
        public sealed class PayrollPreviewDto
        {
            public long EmployeeId { get; set; }
            public string EmployeeCode { get; set; } = "";
            public string FullName { get; set; } = "";
            public DateTime WorkDate { get; set; }
            public string ShiftCode { get; set; } = "";
            public DateTime? CheckInTime { get; set; }
            public DateTime? CheckOutTime { get; set; }
            public int MinutesWorked { get; set; }
            public int LateMinutes { get; set; }
            public decimal GrossPay { get; set; }
            public decimal PenaltyAmount { get; set; }
            public decimal NetPay { get; set; }
        }


}
