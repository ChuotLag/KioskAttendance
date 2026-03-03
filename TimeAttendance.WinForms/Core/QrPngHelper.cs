using QRCoder;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TimeAttendance.WinForms.Core
{
    internal class QrPngHelper
    {
        public static string ToBase64Png(string text)
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
            var qr = new PngByteQRCode(data);
            byte[] bytes = qr.GetGraphic(10);
            return Convert.ToBase64String(bytes);
        }
    }
}
