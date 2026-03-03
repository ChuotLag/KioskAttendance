using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using TimeAttendance.WinForms.Core;
using TimeAttendance.WinForms.Infrastructure;

namespace TimeAttendance.WinForms;

/// <summary>
/// Native WinForms kiosk screen: select employee from DB + enter 4-digit PIN for Check-in/Check-out.
/// (No API / WebView required.)
/// </summary>
public sealed class AttendanceKioskForm : Form
{
    private readonly ILogger<AttendanceKioskForm> _logger;
    private readonly IConfiguration _config;
    private readonly IEmployeeRepository _employees;
    private readonly IAttendanceRepository _attendance;
    private readonly IKioskRepository _kiosk;

    private readonly ComboBox _cboEmployee = new();
    private readonly TextBox _txtPin = new();
    private readonly Button _btnCheckIn = new();
    private readonly Button _btnCheckOut = new();
    private readonly Label _lblStatus = new();

    private Guid _deviceGuid;
    private string _deviceCode = "KIOSK1";
    private string _deviceName = "Quầy 1";

    public AttendanceKioskForm(
        ILogger<AttendanceKioskForm> logger,
        IConfiguration config,
        IEmployeeRepository employees,
        IAttendanceRepository attendance,
        IKioskRepository kiosk)
    {
        _logger = logger;
        _config = config;
        _employees = employees;
        _attendance = attendance;
        _kiosk = kiosk;

        Text = "Chấm công (PIN)";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 420);

        BuildUi();

        Shown += async (_, __) => await InitAsync();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            Padding = new Padding(18),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        for (int i = 0; i < root.RowCount; i++)
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "MÀN HÌNH CHẤM CÔNG (PIN 4 SỐ)",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.FontFamily, 16, FontStyle.Bold),
            AutoSize = true,
        };
        root.Controls.Add(title, 0, 0);
        root.SetColumnSpan(title, 2);

        root.Controls.Add(new Label { Text = "Nhân viên", AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(Font, FontStyle.Bold) }, 0, 1);
        _cboEmployee.Dock = DockStyle.Fill;
        _cboEmployee.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboEmployee.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _cboEmployee.AutoCompleteSource = AutoCompleteSource.ListItems;
        root.Controls.Add(_cboEmployee, 1, 1);

        root.Controls.Add(new Label { Text = "PIN (4 số)", AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(Font, FontStyle.Bold) }, 0, 2);
        _txtPin.Dock = DockStyle.Left;
        _txtPin.Width = 220;
        _txtPin.MaxLength = 4;
        _txtPin.PasswordChar = '●';
        _txtPin.Font = new Font(Font.FontFamily, 18, FontStyle.Bold);
        _txtPin.KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true;
        };
        _txtPin.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await DoCheckInAsync();
            }
        };
        root.Controls.Add(_txtPin, 1, 2);

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 10, 0, 0)
        };

        _btnCheckIn.Text = "CHECK IN";
        _btnCheckIn.Width = 200;
        _btnCheckIn.Height = 56;
        _btnCheckIn.Font = new Font(Font.FontFamily, 14, FontStyle.Bold);
        _btnCheckIn.Click += async (_, __) => await DoCheckInAsync();

        _btnCheckOut.Text = "CHECK OUT";
        _btnCheckOut.Width = 200;
        _btnCheckOut.Height = 56;
        _btnCheckOut.Font = new Font(Font.FontFamily, 14, FontStyle.Bold);
        _btnCheckOut.Click += async (_, __) => await DoCheckOutAsync();

        btnPanel.Controls.Add(_btnCheckIn);
        btnPanel.Controls.Add(_btnCheckOut);

        root.Controls.Add(new Label { Text = "", AutoSize = true }, 0, 3);
        root.Controls.Add(btnPanel, 1, 3);

        _lblStatus.Text = "Sẵn sàng.";
        _lblStatus.Dock = DockStyle.Fill;
        _lblStatus.AutoSize = true;
        _lblStatus.Padding = new Padding(0, 16, 0, 0);
        _lblStatus.Font = new Font(Font.FontFamily, 11, FontStyle.Regular);
        root.Controls.Add(_lblStatus, 0, 4);
        root.SetColumnSpan(_lblStatus, 2);

        var hint = new Label
        {
            Text = "Tip: Nhập PIN rồi bấm Enter để CHECK IN nhanh.",
            Dock = DockStyle.Fill,
            AutoSize = true,
            ForeColor = Color.DimGray
        };
        root.Controls.Add(hint, 0, 5);
        root.SetColumnSpan(hint, 2);

        Controls.Add(root);
    }

    private async Task InitAsync()
    {
        try
        {
            _lblStatus.Text = "Đang khởi tạo...";
            _deviceCode = _config["Kiosk:DeviceCode"] ?? "KIOSK1";
            _deviceName = _config["Kiosk:DeviceName"] ?? "Quầy 1";

            if (!Guid.TryParse(_config["Kiosk:DeviceGuid"], out _deviceGuid) || _deviceGuid == Guid.Empty)
            {
                // Tạo GUID ổn định theo DeviceCode + machine name (tránh thay đổi mỗi lần chạy)
                _deviceGuid = StableGuid($"{Environment.MachineName}|{_deviceCode}");
            }

            await _kiosk.EnsureKioskAsync(_deviceGuid, _deviceCode, _deviceName);
            await LoadEmployeesAsync();

            _lblStatus.Text = "Sẵn sàng điểm danh.";
            _txtPin.Focus();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Init kiosk failed");
            MessageBox.Show(ex.Message, "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _lblStatus.Text = "Không khởi tạo được.";
        }
    }

    private async Task LoadEmployeesAsync()
    {
        var list = await _employees.ListAsync(includeInactive: false);

        _cboEmployee.DisplayMember = nameof(EmployeeDto.FullName);
        _cboEmployee.ValueMember = nameof(EmployeeDto.EmployeeCode);

        // Hiển thị "CODE - Name" cho dễ chọn
        _cboEmployee.DataSource = list
            .Select(e => new EmployeeListItem(e.EmployeeCode, e.FullName))
            .ToList();

        if (_cboEmployee.Items.Count > 0)
            _cboEmployee.SelectedIndex = 0;
    }

    private sealed record EmployeeListItem(string EmployeeCode, string FullName)
    {
        public override string ToString() => $"{EmployeeCode} - {FullName}";
    }

    private EmployeeListItem? SelectedEmployee => _cboEmployee.SelectedItem as EmployeeListItem;

    private bool ValidateInputs(out string employeeCode, out string pin)
    {
        employeeCode = SelectedEmployee?.EmployeeCode ?? "";
        pin = (_txtPin.Text ?? "").Trim();

        if (string.IsNullOrWhiteSpace(employeeCode))
        {
            MessageBox.Show("Vui lòng chọn nhân viên.", "Thiếu thông tin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _cboEmployee.Focus();
            return false;
        }

        if (pin.Length != 4 || pin.Any(c => !char.IsDigit(c)))
        {
            MessageBox.Show("PIN phải đúng 4 chữ số.", "PIN không hợp lệ", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtPin.Focus();
            _txtPin.SelectAll();
            return false;
        }

        return true;
    }

    private async Task DoCheckInAsync()
    {
        if (!ValidateInputs(out var employeeCode, out var pin)) return;
        await RunAsync("CHECK IN", async () =>
        {
            await _attendance.CheckInAsync(employeeCode, pin, _deviceGuid);
            _lblStatus.Text = $"✅ Check-in thành công ({employeeCode}) lúc {DateTime.Now:HH:mm:ss}";
        });
    }

    private async Task DoCheckOutAsync()
    {
        if (!ValidateInputs(out var employeeCode, out var pin)) return;
        await RunAsync("CHECK OUT", async () =>
        {
            await _attendance.CheckOutAsync(employeeCode, pin, _deviceGuid);
            _lblStatus.Text = $"✅ Check-out thành công ({employeeCode}) lúc {DateTime.Now:HH:mm:ss}";
        });
    }

    private async Task RunAsync(string actionName, Func<Task> action)
    {
        ToggleUi(false);
        try
        {
            _lblStatus.Text = $"Đang {actionName.ToLowerInvariant()}...";
            await action();
            _txtPin.Clear();
            _txtPin.Focus();
        }
        catch (AppDomainException ex)
        {
            _logger.LogWarning(ex, "{Action} failed", actionName);
            _lblStatus.Text = "❌ Không thành công.";
            MessageBox.Show(ex.Message, "Điểm danh thất bại", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtPin.Focus();
            _txtPin.SelectAll();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Action} error", actionName);
            _lblStatus.Text = "❌ Lỗi hệ thống.";
            MessageBox.Show(ex.Message, "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            ToggleUi(true);
        }
    }

    private void ToggleUi(bool enabled)
    {
        _cboEmployee.Enabled = enabled;
        _txtPin.Enabled = enabled;
        _btnCheckIn.Enabled = enabled;
        _btnCheckOut.Enabled = enabled;
    }

    private static Guid StableGuid(string input)
    {
        // SHA1 -> lấy 16 bytes đầu tạo GUID ổn định
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(input));
        var g = new byte[16];
        Array.Copy(bytes, g, 16);
        return new Guid(g);
    }
}
