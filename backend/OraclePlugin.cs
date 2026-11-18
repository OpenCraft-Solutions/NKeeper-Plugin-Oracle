using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Abstractions;
using NKeeper.Plugins.Databases;

namespace NKeeper.Plugins.Oracle;

/// <summary>
/// The oracle plugin (docs/architecture.md §7.34): the <c>oracle</c> service type and its database tab, which db-core
/// runs and draws (§7.32). Core opens the connections and signs in (§7.31); this plugin never sees the password.
/// </summary>
public sealed class OraclePlugin : IPlugin
{
    public const string ServiceType = "oracle";

    public const string SessionType = "oracle";

    /// <summary>The database's service name, or its SID: what the listener knows it by.</summary>
    public const string ServiceNameSetting = "serviceName";

    public void Contribute(IPluginContributions contributions, PluginContext context)
    {
        contributions.AddServiceType(new ServiceType(ServiceType, "Oracle")
        {
            DefaultPort = 1521,
            Health = HealthProbe.Tcp,
            Settings = [ServiceSetting.Text(ServiceNameSetting, "Service name or SID", maxLength: 128, required: true)],
        });
        contributions.AddSessionType(new SessionType(SessionType, "Oracle", typeof(DatabaseSessionHandler<OracleEngine>)) { ServiceTypes = [ServiceType] });
        contributions.AddHostAction(new HostAction("oracle.query", "Oracle", SessionType: SessionType));
    }

    public void ConfigureServices(IServiceCollection services, PluginContext context) => services.AddDatabaseEngine<OracleEngine>();

    public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context)
    {
    }
}
