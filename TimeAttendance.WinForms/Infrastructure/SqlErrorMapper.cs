using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TimeAttendance.WinForms.Infrastructure
{
    public sealed class AppDomainException : Exception
    {
        public int ErrorCode { get; }
        public AppDomainException(int code, string message) : base(message) => ErrorCode = code;
    }

    public static class SqlErrorMapper
    {
        public static AppDomainException ToDomainException(SqlException ex)
        {
            return ex.Number switch
            {
                52004 => new AppDomainException(ex.Number, "Sai PIN."),
                52006 => new AppDomainException(ex.Number, "Không tìm thấy lịch làm việc nào được sắp xếp cho nhân viên này (hôm nay/hôm qua)."),
                52007 => new AppDomainException(ex.Number, "Bạn đã check-in ca này rồi."),
                53004 => new AppDomainException(ex.Number, "Sai PIN."),
                53006 => new AppDomainException(ex.Number, "Không tìm thấy lịch làm việc nào được sắp xếp cho nhân viên này (hôm nay/hôm qua)."),
                53007 => new AppDomainException(ex.Number, "Không có ca đang mở để check-out."),
                // Employee CRUD
                54001 => new AppDomainException(ex.Number, "Mã nhân viên (EmployeeCode) không được trống."),
                54002 => new AppDomainException(ex.Number, "Tên nhân viên (FullName) không được trống."),
                54003 => new AppDomainException(ex.Number, "Mã nhân viên đã tồn tại."),
                54005 => new AppDomainException(ex.Number, "Lương/giờ phải lớn hơn 0."),
                54010 => new AppDomainException(ex.Number, "Thiếu EmployeeId."),
                54011 => new AppDomainException(ex.Number, "Không tìm thấy nhân viên."),
                54020 => new AppDomainException(ex.Number, "Không thể xóa vĩnh viễn vì đã phát sinh chấm công/lương."),

                _ => new AppDomainException(ex.Number, ex.Message)


            };
        }
    }
}
