using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TimeAttendance.WinForms.Infrastructure
{
    internal class TSqlConnectionFactory
    {
        private readonly string _connectionString;

        public TSqlConnectionFactory(string connectionString)
        {
            _connectionString = connectionString;
        }

        public IDbConnection Create() => new SqlConnection(_connectionString);

    }
}
