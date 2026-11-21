using System.Data.Common;
using MySqlConnector;
using VIPCore.Configs;

namespace VIPCore;

public class DatabaseProvider(Config<VipConfig> coreConfig)
{
    public string ConnectionString { get; } = BuildConnectionString(coreConfig.Value.Connection);

    private static string BuildConnectionString(VipConnection data)
    {
        return new MySqlConnectionStringBuilder
        {
            Server = data.Host,
            Database = data.Database,
            UserID = data.User,
            Password = data.Password,
            Port = data.Port,
            Pooling = true,
            MinimumPoolSize = 0,
            // Next-il: was 640 per server - with ~60 servers on one database that is far past what it allows.
            // The core makes a few short queries per connect and one per 30s sync, so a handful is plenty.
            MaximumPoolSize = 10,
            ConnectionIdleTimeout = 30,
            ConnectionTimeout = 10,
            DefaultCommandTimeout = 15
        }.ConnectionString;
    }

    public async Task<DbConnection> OpenConnectionAsync()
    {
        var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
