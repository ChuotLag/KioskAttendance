using Dapper;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using TimeAttendance.WinForms.Core;

namespace TimeAttendance.WinForms.Infrastructure;

public interface IEmployeeRepository
{
    Task<List<EmployeeDto>> ListAsync(bool includeInactive);
    Task<EmployeeDto> CreateAsync(EmployeeCreatePayload payload);
    Task<EmployeeDto> UpdateAsync(EmployeeUpdatePayload payload, CancellationToken ct);
    Task DeleteAsync(long employeeId, bool hardDelete);
}

public sealed class EmployeeRepository : IEmployeeRepository
{
    private readonly ISqlConnectionFactory _db;

    public EmployeeRepository(ISqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<List<EmployeeDto>> ListAsync(bool includeInactive)
    {
        try
        {
            using var conn = _db.Create();
            var rows = await conn.QueryAsync<EmployeeDto>(
                "dbo.usp_Employee_List",
                new { IncludeInactive = includeInactive },
                commandType: CommandType.StoredProcedure);
            return rows.ToList();
        }
        catch (SqlException ex)
        {
            throw SqlErrorMapper.ToDomainException(ex);
        }
    }

    public async Task<EmployeeDto> CreateAsync(EmployeeCreatePayload payload)
    {
        try
        {
            using var conn = _db.Create();
            var p = new DynamicParameters();
           /* p.Add("@EmployeeCode", payload.EmployeeCode);*/
            p.Add("@FullName", payload.FullName);
            p.Add("@Phone", payload.Phone);
            p.Add("@HourlyRate", payload.HourlyRate);
            p.Add("@Pin", payload.Pin);
            return await conn.QueryFirstAsync<EmployeeDto>(
                "dbo.usp_Employee_Create",
                p,
                commandType: CommandType.StoredProcedure);
        }
        catch (SqlException ex)
        {
            throw SqlErrorMapper.ToDomainException(ex);
        }
    }

    public async Task<EmployeeDto> UpdateAsync(EmployeeUpdatePayload payload, CancellationToken ct)
    {
        try
        {
            using var conn = _db.Create();
            var p = new DynamicParameters();
            p.Add("@EmployeeId", payload.EmployeeId, dbType: System.Data.DbType.Int64);
            /*p.Add("@EmployeeCode", payload.EmployeeCode);*/
            p.Add("@FullName", payload.FullName);
            p.Add("@Phone", payload.Phone);
            p.Add("@HourlyRate", payload.HourlyRate);
            p.Add("@IsActive", payload.IsActive);
            p.Add("@Pin", payload.Pin);

            return await conn.QueryFirstAsync<EmployeeDto>(
                "dbo.usp_Employee_Update",
                p,
                commandType: CommandType.StoredProcedure);
        }
        catch (SqlException ex)
        {
            throw SqlErrorMapper.ToDomainException(ex);
        }
    }

    public async Task DeleteAsync(long employeeId, bool hardDelete)
    {
        try
        {
            using var conn = _db.Create();
            await conn.ExecuteAsync(
                "dbo.usp_Employee_Delete",
                new { EmployeeId = employeeId, HardDelete = hardDelete },
                commandType: CommandType.StoredProcedure);
        }
        catch (SqlException ex)
        {
            throw SqlErrorMapper.ToDomainException(ex);
        }
    }
}
