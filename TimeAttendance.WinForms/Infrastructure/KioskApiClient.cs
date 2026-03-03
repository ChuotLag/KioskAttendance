using System;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using TimeAttendance.WinForms.Core;
using static System.Net.WebRequestMethods;

namespace TimeAttendance.WinForms.Infrastructure
{

        public interface IKioskApiClient
        {
            Task<KioskTokenDto> GetKioskTokenAsync(string kioskCode, CancellationToken ct = default);
        }

    public sealed class KioskApiClient : IKioskApiClient
    {
        private readonly HttpClient _http;
        private readonly IConfiguration? _cfg;

        public KioskApiClient(HttpClient http, IConfiguration? cfg = null)
        {
            _http = http;
            _cfg = cfg;
        }

   /*     public async Task<KioskTokenDto> GetKioskTokenAsync(string kioskCode, CancellationToken ct = default)
        {
            // API endpoint: GET /api/kiosk/token?k=KIOSK1
            var path = $"/api/kiosk/token?k={Uri.EscapeDataString(kioskCode)}";

            *//*  var dto = await _http.GetFromJsonAsync<KioskTokenDto>(path, cancellationToken: ct);
              if (dto == null || string.IsNullOrWhiteSpace(dto.Url))
                  throw new InvalidOperationException("Kiosk token response is empty.");

              return dto;*//*
            var dto = await _http.GetFromJsonAsync<KioskTokenDto>(path, cancellationToken: ct);

            // validate theo DTO mới
            if (dto is null || string.IsNullOrWhiteSpace(dto.C) || string.IsNullOrWhiteSpace(dto.Sig))
                throw new InvalidOperationException("Kiosk token rỗng (c/s). Kiểm tra API /api/kiosk/token trả về.");

            return dto;
        }*/

        public async Task<KioskTokenDto> GetKioskTokenAsync(string kioskCode, CancellationToken ct = default)
        {
            // Allow runtime config change (no rebuild): update BaseAddress from IConfiguration if provided.
            var baseUrl = _cfg?["Api:BaseUrl"];
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                try { _http.BaseAddress = new Uri(baseUrl); } catch { /* ignore */ }
            }

            var path = $"/api/kiosk/token?k={Uri.EscapeDataString(kioskCode)}";
            var dto = await _http.GetFromJsonAsync<KioskTokenDto>(path, cancellationToken: ct);

            if (dto is null)
                throw new InvalidOperationException("Kiosk token null.");

            // CHỈ fail nếu vừa không có Url, vừa không có c/s
            if (string.IsNullOrWhiteSpace(dto.Url) &&
                (string.IsNullOrWhiteSpace(dto.TokenC) || string.IsNullOrWhiteSpace(dto.TokenSig)))
                throw new InvalidOperationException("Kiosk token rỗng (url hoặc c/s). Kiểm tra API /api/kiosk/token trả về.");

            return dto;
        }



    }


}
