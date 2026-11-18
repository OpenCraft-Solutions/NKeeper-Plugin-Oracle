using System.Data.Common;
using System.Text.Json;
using NKeeper.Abstractions;
using NKeeper.Plugins.Databases;
using Oracle.ManagedDataAccess.Client;

namespace NKeeper.Plugins.Oracle;

/// <summary>
/// Oracle (11g and later) for db-core (docs/architecture.md §7.34), ported from 1.x's <c>OracleConnector</c>: the
/// service's service name (or SID) and a database login, which core signs in with ODP.NET (SDK 1.7). Oracle's native
/// network encryption and integrity checks are requested, so a server that offers them encrypts the connection. A login
/// named SYS connects AS SYSDBA.
/// </summary>
public sealed class OracleEngine(IConnectivity connectivity, ICredentialBroker broker) : IDatabaseEngine
{
    public const string PluginId = "oracle";

    private const int ConnectTimeoutSeconds = 20;

    /// <summary>
    /// ODP.NET takes these process-wide settings only before its first connection. It loads in this plugin's own
    /// context, so the engine comes first; should something have opened a connection already, the server's own
    /// defaults apply.
    /// </summary>
    static OracleEngine()
    {
        try
        {
            OracleConfiguration.SqlNetEncryptionClient = "requested";
            OracleConfiguration.SqlNetCryptoChecksumClient = "requested";
        }
        catch (InvalidOperationException)
        {
            // ORA-50099: a connection is already open.
        }
    }

    string IDatabaseEngine.PluginId => PluginId;

    public string Product => "Oracle";

    public IDatabaseDialect Dialect { get; } = new OracleDialect();

    public async Task<IDatabaseLogin> OpenAsync(StreamSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var handle = request.Credential ?? throw new ConnectException(ConnectFailure.NoSecret, "Choose a credential, or type one, to sign in to Oracle.");
        var described = await broker.DescribeAsync(request.Host, request.ServiceId, handle, cancellationToken);
        if (described.Domain is { Length: > 0 })
        {
            throw new ConnectException(ConnectFailure.AuthenticationFailed, "Oracle logins have no domain: use a credential without one.");
        }

        var service = request.Host.Services.FirstOrDefault(s => s.Id == request.ServiceId);
        var serviceName = service is not null && service.Settings.ValueKind == JsonValueKind.Object
            && service.Settings.TryGetProperty(OraclePlugin.ServiceNameSetting, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;
        if (string.IsNullOrEmpty(serviceName))
        {
            throw new ConnectException(ConnectFailure.Unreachable, "This Oracle service has no service name. Edit the host to add it (e.g. ORCLPDB1).");
        }

        var sysdba = string.Equals(described.Username, "SYS", StringComparison.OrdinalIgnoreCase);
        DatabaseConnectRequest Connect(string connectData) => new(
            OracleClientFactory.Instance,
            endpoint => new OracleConnectionStringBuilder
            {
                DataSource = $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={endpoint.Address})(PORT={endpoint.Port}))(CONNECT_DATA=({connectData}={serviceName})))",
                // Each session holds its own connections; a pool would keep logins open after it ends.
                Pooling = false,
                ConnectionTimeout = ConnectTimeoutSeconds,
                DBAPrivilege = sysdba ? "SYSDBA" : string.Empty,
            },
            "User Id",
            "Password")
        {
            ConnectTimeout = TimeSpan.FromSeconds(ConnectTimeoutSeconds + 5),
        };

        try
        {
            return await connectivity.OpenDatabaseAsync(request.Host, request.ServiceId, handle, Connect("SERVICE_NAME"), cancellationToken);
        }
        catch (ConnectException ex) when (ex.Message.Contains("ORA-12514", StringComparison.Ordinal))
        {
            // Not a service the listener knows: it may be a SID (older databases are often reached by one).
            try
            {
                return await connectivity.OpenDatabaseAsync(request.Host, request.ServiceId, handle, Connect("SID"), cancellationToken);
            }
            catch (ConnectException)
            {
                throw Explained(ex, described.Username, serviceName);
            }
        }
        catch (ConnectException ex)
        {
            throw Explained(ex, described.Username, serviceName);
        }
    }

    /// <summary>A refused sign-in, in words, as 1.x said it; anything else as core said it.</summary>
    private static ConnectException Explained(ConnectException ex, string? username, string serviceName)
    {
        string? Says(string code, string message) => ex.Message.Contains(code, StringComparison.Ordinal) ? message : null;
        var message =
            Says("ORA-01017", $"Oracle refused the login “{username}”. Check the username and password.")
            ?? Says("ORA-28000", $"The account “{username}” is locked.")
            ?? Says("ORA-28001", $"The password of “{username}” has expired. Change it in another client, then connect again.")
            ?? Says("ORA-01045", $"“{username}” lacks CREATE SESSION, so it can't sign in.")
            ?? Says("ORA-12514", $"The listener doesn't know the service name or SID “{serviceName}”. Check the service's name on the host.")
            ?? Says("ORA-12505", $"The listener doesn't know the service name or SID “{serviceName}”. Check the service's name on the host.");
        return message is null ? ex : new ConnectException(ex.Reason, message);
    }
}

/// <summary>What the Oracle dialect adds for db-core: what the server says about itself, and DBMS_OUTPUT on each window.</summary>
public sealed partial class OracleDialect
{
    public async Task<DatabaseServerInfo> ServerInfoAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var row = (await DbQuery.ListAsync(
            connection,
            "SELECT SYS_CONTEXT('USERENV','INSTANCE_NAME'), SYS_CONTEXT('USERENV','DB_NAME'), SYS_CONTEXT('USERENV','SESSION_USER'), SYS_CONTEXT('USERENV','SID') FROM dual",
            [],
            r => (Instance: DbQuery.Text(r, 0), Database: DbQuery.Text(r, 1), Login: DbQuery.Text(r, 2), Sid: DbQuery.Int(r, 3)),
            cancellationToken))[0];

        string? edition = null;
        try
        {
            // V$VERSION needs SELECT_CATALOG_ROLE (or similar); without it, only the version number is shown.
            var banners = await DbQuery.ListAsync(connection, "SELECT banner FROM v$version WHERE ROWNUM = 1", [], r => DbQuery.Text(r, 0), cancellationToken);
            var banner = banners.Count > 0 ? banners[0] : null;
            edition = banner?.Split(" Release ")[0].Replace("Oracle Database ", string.Empty, StringComparison.Ordinal).Trim();
        }
        catch (DbException)
        {
        }

        var name = row.Instance is null ? connection.DataSource : row.Database is null || string.Equals(row.Instance, row.Database, StringComparison.OrdinalIgnoreCase)
            ? row.Instance
            : $"{row.Instance} ({row.Database})";
        return new DatabaseServerInfo(name, connection.ServerVersion, null, edition, row.Login ?? string.Empty, row.Sid);
    }

    /// <summary>DBMS_OUTPUT (PUT_LINE) is shown under Messages, as SQL Developer does with it on.</summary>
    public Task PrepareWindowAsync(DbConnection connection, CancellationToken cancellationToken) =>
        DbQuery.ExecuteAsync(connection, "BEGIN DBMS_OUTPUT.ENABLE(NULL); END;", cancellationToken);
}
