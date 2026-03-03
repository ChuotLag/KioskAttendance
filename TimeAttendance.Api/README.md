# TimeAttendance.Api (QR dong + Mobile cham cong)

## Muc tieu
- May quan (192.168.1.62) chay API + sinh QR dong (doi moi 30 giay).
- Nhan vien dung dien thoai quet QR -> mo trang cham cong -> chon nhan vien + nhap PIN -> Check-in/Check-out.
- API goi stored procedure trong SQL Server: `att.usp_CheckIn` va `att.usp_CheckOut`.

## Chay nhanh
1) Chay script SQL `sql_Attendance.sql` trong dung Database.
2) Mo file `TimeAttendance.Api/appsettings.json` va sua:
   - `ConnectionStrings:Db` cho dung SQL Server/Database.
3) Trenet may quan (IP `192.168.1.62`), mo Solution va chay project `TimeAttendance.Api`.
4) Mo tren trinh duyet tai quan:
   - Kiosk QR: `http://192.168.1.62:5000/`
5) Nhan vien ket noi Wi-Fi/LAN cua quan -> quet QR -> cham cong.

## Luu y quan trong
- API mac dinh chi chap nhan client IP bat dau bang `192.168.1.` (xem `Server:AllowedSubnetPrefix`).
- Neu doi dai IP LAN, doi gia tri nay trong appsettings.
- Neu QR bao het han: quet lai (QR doi moi 30 giay).
