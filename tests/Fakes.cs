using System.Data.Common;
using System.Text.Json;
using NKeeper.Abstractions;
using Oracle.ManagedDataAccess.Client;
using Testcontainers.Oracle;

[assembly: AssemblyFixture(typeof(NKeeper.Plugins.Oracle.Tests.OracleFixture))]

namespace NKeeper.Plugins.Oracle.Tests;

/// <summary>
/// A real Oracle Database Free (gvenzl/oracle-free in Docker), with an application user and SYSTEM. Tests are skipped,
/// not passed, when Docker is unavailable.
/// </summary>
public sealed class OracleFixture : IAsyncLifetime
{
    public const string Address = "127.0.0.1";
    public const string ServiceName = "FREEPDB1";
    public const string UserName = "NKEEPER";
    public const string Password = "Oracle-Test-Passw0rd";

    private OracleContainer? _container;

    public string? UnavailableReason { get; private set; }

    public int Port => _container!.GetMappedPublicPort(1521);

    public async ValueTask InitializeAsync()
    {
        // The engine's network encryption settings, before the tests' own connections, as in the server.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(OracleEngine).TypeHandle);
        try
        {
            _container = new OracleBuilder("gvenzl/oracle-free:23-slim-faststart").WithUsername(UserName).WithPassword(Password).Build();
            await _container.StartAsync();
        }
#pragma warning disable CA1031 // Any failure here means "no Oracle available", which is reported as a skip.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            UnavailableReason = $"Oracle tests need Docker (Testcontainers). Docker was not usable: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public void SkipIfUnavailable()
    {
        if (_container is null || UnavailableReason is not null)
        {
            Assert.Skip(UnavailableReason ?? "Oracle is not available.");
        }
    }

    /// <summary>A connection of the test's own, as the application user or SYSTEM, to set things up or to hold locks.</summary>
    public async Task<OracleConnection> OpenAsync(string user = UserName, CancellationToken cancellationToken = default)
    {
        var connection = new OracleConnection(new OracleConnectionStringBuilder
        {
            DataSource = $"{Address}:{Port}/{ServiceName}",
            UserID = user,
            Password = Password,
            Pooling = false,
        }.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public async Task ExecuteAsync(string sql, string user = UserName, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(user, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>Core's side, as the engine sees it (docs/architecture.md §7.31): a login opened with the engine's driver and the credential's password.</summary>
internal sealed class FakeConnectivity(int port, string username, string password) : IConnectivity
{
    public int Port => port;

    public string Username => username;

    public string Password => password;

    /// <summary>The connection strings core opened, as the driver left them.</summary>
    public List<string> Opened { get; } = [];

    public async Task<IDatabaseLogin> OpenDatabaseAsync(HostContext host, Guid serviceId, CredentialHandle handle, DatabaseConnectRequest request, CancellationToken cancellationToken = default)
    {
        var login = new Login(this, request);
        await login.OpenAnotherAsync(cancellationToken);
        return login;
    }

    public Task<ISshConnection> OpenSshAsync(HostContext host, Guid serviceId, CredentialHandle handle, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IHttpConnection> OpenHttpAsync(HostContext host, Guid serviceId, bool tls, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IDesktopConnection> OpenDesktopAsync(HostContext host, Guid serviceId, CredentialHandle handle, DesktopOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    private sealed class Login(FakeConnectivity core, DatabaseConnectRequest request) : IDatabaseLogin
    {
        private readonly List<DbConnection> _opened = [];

        public DbConnection Connection => _opened[0];

        public async Task<DbConnection> OpenAnotherAsync(CancellationToken cancellationToken = default)
        {
            var builder = request.Build(new DatabaseEndpoint(OracleFixture.Address, core.Port));
            builder[request.UserKeyword] = core.Username;
            builder[request.PasswordKeyword] = core.Password;
            builder["Persist Security Info"] = false;
            var connection = request.Factory.CreateConnection()!;
            connection.ConnectionString = builder.ConnectionString;
            try
            {
                await connection.OpenAsync(cancellationToken);
            }
            catch (DbException ex)
            {
                await connection.DisposeAsync();
                // As core reports it: the driver's message with its inner exceptions', without the password.
                var messages = string.Join(" ", Chain(ex).Select(e => e.Message.Trim()).Distinct());
                throw new ConnectException(ConnectFailure.AuthenticationFailed, $"{OracleFixture.Address}:{core.Port}: {messages.Replace(core.Password, "***", StringComparison.Ordinal)}");
            }

            core.Opened.Add(connection.ConnectionString);
            _opened.Add(connection);
            return connection;
        }

        private static IEnumerable<Exception> Chain(Exception ex)
        {
            for (var e = ex; e is not null; e = e.InnerException)
            {
                yield return e;
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var connection in _opened)
            {
                await connection.DisposeAsync();
            }
        }
    }
}

/// <summary>The credential broker's description of the handle: its user name, and a domain or none.</summary>
internal sealed class FakeBroker(string username, string? domain = null) : ICredentialBroker
{
    public Task<CredentialHandle> ResolveSavedAsync(HostContext host, Guid serviceId, Guid credentialId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CredentialHandle> ResolvePromptAsync(HostContext host, Guid serviceId, string promptReference, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CredentialDescription> DescribeAsync(HostContext host, Guid serviceId, CredentialHandle handle, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CredentialDescription(CredentialSource.Prompt, null, username, domain, true, false));
}

internal static class Requests
{
    public static readonly Guid ServiceId = Guid.NewGuid();

    public static StreamSessionRequest Session(string serviceName = OracleFixture.ServiceName)
    {
        var settings = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { [OraclePlugin.ServiceNameSetting] = serviceName })).RootElement;
        var service = new HostService(ServiceId, OraclePlugin.ServiceType, null, 1521, OracleFixture.Address, settings, true);
        var host = new HostContext(Guid.NewGuid(), "ora01", null, OracleFixture.Address, [], HostPermission.Use) { Services = [service] };
        return new StreamSessionRequest(host, ServiceId, Handle(), JsonDocument.Parse("{}").RootElement);
    }

    /// <summary>A handle, as the broker gives one: opaque to the plugin.</summary>
    public static CredentialHandle Handle() =>
        (CredentialHandle)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CredentialHandle));
}
