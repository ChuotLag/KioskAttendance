using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.Data;

namespace TimeAttendance.Api.Infrastructure;

public interface ISqlConnectionFactory
{
    IDbConnection Create();
}

public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("Db")
                            ?? throw new InvalidOperationException("Missing ConnectionStrings:Db in appsettings.json");
    }

    public IDbConnection Create() => new SqlConnection(_connectionString);
}
