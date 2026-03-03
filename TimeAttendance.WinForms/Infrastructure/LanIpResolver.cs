using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace TimeAttendance.WinForms.Infrastructure
{
    internal class LanIpResolver
    {
        public static string GetBestLanIPv4()
        {
            // Cho phép override nhanh khi cần
            var forced = Environment.GetEnvironmentVariable("TA_PUBLIC_IP");
            if (!string.IsNullOrWhiteSpace(forced))
                return forced.Trim();

            var nics = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n =>
                    n.OperationalStatus == OperationalStatus.Up &&
                    n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    n.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                    !IsIgnored(n))
                .Select(n => new
                {
                    Nic = n,
                    Props = n.GetIPProperties(),
                    Ips = n.GetIPProperties().UnicastAddresses
                        .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(u => u.Address)
                        .Where(IsUsableLanIp)
                        .ToList()
                })
                .Where(x => x.Ips.Count > 0)
                .ToList();

            if (nics.Count == 0)
                return "127.0.0.1";

            // Ưu tiên IP Mobile Hotspot mặc định của Windows: 192.168.137.1
            var hotspot = nics.SelectMany(x => x.Ips).FirstOrDefault(ip => ip.ToString() == "192.168.137.1");
            if (hotspot != null) return hotspot.ToString();

            // Ưu tiên NIC có gateway (thường là LAN/WiFi thật)
            var withGw = nics
                .Where(x => x.Props.GatewayAddresses.Any(g => g.Address != null && !g.Address.Equals(IPAddress.Any)))
                .ToList();

            // Nếu có gateway: ưu tiên Wireless trước, rồi Ethernet
            var ordered = withGw.Count > 0 ? withGw : nics;

            var pick = ordered
                .OrderByDescending(x => x.Nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .ThenByDescending(x => x.Nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                .SelectMany(x => x.Ips)
                .FirstOrDefault();

            return pick?.ToString() ?? "127.0.0.1";
        }

        private static bool IsUsableLanIp(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip)) return false;

            var b = ip.GetAddressBytes();
            // APIPA 169.254.x.x
            if (b[0] == 169 && b[1] == 254) return false;

            // Private ranges: 10.x.x.x / 172.16-31.x.x / 192.168.x.x
            bool isPrivate =
                b[0] == 10 ||
                (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                (b[0] == 192 && b[1] == 168);

            return isPrivate;
        }

        private static bool IsIgnored(NetworkInterface n)
        {
            var name = (n.Name ?? "").ToLowerInvariant();
            var desc = (n.Description ?? "").ToLowerInvariant();

            // loại bớt adapter ảo hay VPN (tuỳ môi trường)
            string[] bad = { "virtual", "vmware", "hyper-v", "vpn", "loopback", "tunnel" };
            return bad.Any(k => name.Contains(k) || desc.Contains(k));
        }
    }
}
