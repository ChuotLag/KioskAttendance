using Microsoft.Data.SqlClient;
using System.Data;

namespace TimeAttendance.WinForms.Infrastructure;

/// <summary>
/// CRUD for pay.PayRateMultipliers using stored procedures:
/// - pay.usp_PayMultiplier_List
/// - pay.usp_PayMultiplier_Upsert
/// - pay.usp_PayMultiplier_Delete
/// </summary>
public sealed class PayMultiplierRepository
{
    private readonly string _cs;
    public PayMultiplierRepository(string connectionString) => _cs = connectionString;

    public async Task<List<object>> ListAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var list = new List<object>();

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand("pay.usp_PayMultiplier_List", conn);
        cmd.CommandType = CommandType.StoredProcedure;

        cmd.Parameters.Add("@DateFrom", SqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
        cmd.Parameters.Add("@DateTo", SqlDbType.Date).Value = to.ToDateTime(TimeOnly.MinValue);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new
            {
                WorkDate = r.GetDateTime(r.GetOrdinal("WorkDate")).ToString("yyyy-MM-dd"),
                Multiplier = r.GetDecimal(r.GetOrdinal("Multiplier")),
                Note = r.IsDBNull(r.GetOrdinal("Note")) ? null : r.GetString(r.GetOrdinal("Note")),
            });
        }

        return list;
    }

    public async Task UpsertAsync(DateOnly workDate, decimal multiplier, string? note, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand("pay.usp_PayMultiplier_Upsert", conn);
        cmd.CommandType = CommandType.StoredProcedure;

        cmd.Parameters.Add("@WorkDate", SqlDbType.Date).Value = workDate.ToDateTime(TimeOnly.MinValue);
        cmd.Parameters.Add("@Multiplier", SqlDbType.Decimal).Value = multiplier;
        cmd.Parameters["@Multiplier"].Precision = 10;
        cmd.Parameters["@Multiplier"].Scale = 2;
        cmd.Parameters.Add("@Note", SqlDbType.NVarChar, 200).Value = (object?)note ?? DBNull.Value;

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(DateOnly workDate, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand("pay.usp_PayMultiplier_Delete", conn);
        cmd.CommandType = CommandType.StoredProcedure;

        cmd.Parameters.Add("@WorkDate", SqlDbType.Date).Value = workDate.ToDateTime(TimeOnly.MinValue);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
