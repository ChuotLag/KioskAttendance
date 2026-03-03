using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace TimeAttendance.WinForms.Infrastructure
{
    public interface ISqlConnectionFactory
    {
        IDbConnection Create();
    }
    public sealed class SqlConnectionFactory : ISqlConnectionFactory
    {
        private readonly string _cs;

        public SqlConnectionFactory(IConfiguration config)
        {
            _cs = config.GetConnectionString("Db")
                  ?? throw new InvalidOperationException("Missing ConnectionStrings:Db in appsettings.json");
        }

        public IDbConnection Create() => new SqlConnection(_cs);
    }
}
