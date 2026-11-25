using System.Text.Json;
using NKeeper.Abstractions;
using NKeeper.Plugins.Databases;

namespace NKeeper.Plugins.Oracle.Tests;

/// <summary>
/// The Oracle engine against a real Oracle Database Free (docs/architecture.md §7.34): signing in, then db-core's
/// session on it, as the engine's session type opens it.
/// </summary>
public sealed class OracleTests(OracleFixture oracle)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Signs_in_by_service_name_and_the_driver_keeps_no_password()
    {
        oracle.SkipIfUnavailable();
        var core = Core();

        await using var login = await new OracleEngine(core, new FakeBroker(OracleFixture.UserName)).OpenAsync(Requests.Session(), Ct);

        Assert.DoesNotContain(OracleFixture.Password, Assert.Single(core.Opened), StringComparison.Ordinal);
        var info = await new OracleDialect().ServerInfoAsync(login.Connection, Ct);
        Assert.Equal(OracleFixture.UserName, info.Login);
        Assert.True(info.SessionId > 0);
    }

    [Fact]
    public async Task A_wrong_password_an_unknown_service_and_a_domain_are_refused_in_words()
    {
        oracle.SkipIfUnavailable();

        var wrong = await Assert.ThrowsAsync<ConnectException>(() => new OracleEngine(Core("not-the-password"), new FakeBroker(OracleFixture.UserName)).OpenAsync(Requests.Session(), Ct));
        Assert.Equal($"Oracle refused the login “{OracleFixture.UserName}”. Check the username and password.", wrong.Message);

        var unknown = await Assert.ThrowsAsync<ConnectException>(() => new OracleEngine(Core(), new FakeBroker(OracleFixture.UserName)).OpenAsync(Requests.Session("NOSUCHDB"), Ct));
        Assert.True(unknown.Message.Contains("doesn't know the service name or SID “NOSUCHDB”", StringComparison.Ordinal), unknown.Message);

        var domain = await Assert.ThrowsAsync<ConnectException>(() => new OracleEngine(Core(), new FakeBroker(OracleFixture.UserName, "CORP")).OpenAsync(Requests.Session(), Ct));
        Assert.Contains("no domain", domain.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scripts_run_statement_by_statement_with_DBMS_OUTPUT_and_errors_at_their_lines()
    {
        oracle.SkipIfUnavailable();
        await using var session = await SessionAsync();
        var window = await session.WindowAsync();
        var table = $"T{Guid.NewGuid():N}"[..20].ToUpperInvariant();

        var result = await session.Channel.ReplyAsync("query", new
        {
            window,
            sql = $"""
                CREATE TABLE {table} (id NUMBER PRIMARY KEY, name VARCHAR2(20));
                INSERT INTO {table} VALUES (1, 'one');
                SELECT id, name, CAST(NULL AS NUMBER) AS nothing FROM {table};
                BEGIN
                  DBMS_OUTPUT.PUT_LINE('hello from PL/SQL');
                END;
                /
                SELECT * FROM does_not_exist;
                """,
        });

        var output = result.GetProperty("output").EnumerateArray().ToList();
        Assert.Equal(["message", "rows", "results", "rows", "message", "message", "error"], output.Select(o => o.GetProperty("kind").GetString()));
        Assert.Equal("Created.", output[0].GetProperty("text").GetString());
        Assert.Equal("(1 row affected)", output[1].GetProperty("text").GetString());
        var rows = output[2];
        Assert.Equal(["ID", "NAME", "NOTHING"], rows.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.True(rows.GetProperty("columns")[0].GetProperty("numeric").GetBoolean());
        Assert.Equal(["1", "one", null], rows.GetProperty("rows")[0].EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("PL/SQL procedure successfully completed.", output[4].GetProperty("text").GetString());
        Assert.Equal("hello from PL/SQL", output[5].GetProperty("text").GetString());
        var error = output[6];
        Assert.Equal((942, 8), (error.GetProperty("number").GetInt32(), error.GetProperty("line").GetInt32()));
        Assert.True(result.GetProperty("hasErrors").GetBoolean());
    }

    [Fact]
    public async Task The_schema_is_the_windows_database_and_a_query_can_be_cancelled()
    {
        oracle.SkipIfUnavailable();
        await using var session = await SessionAsync();
        var window = await session.WindowAsync();

        Assert.Contains("SYSTEM", (await session.Channel.ReplyAsync("databases")).EnumerateArray().Select(d => d.GetString()));
        Assert.Equal("SYSTEM", (await session.Channel.ReplyAsync("use", new { window, name = "SYSTEM" })).GetProperty("name").GetString());
        Assert.Equal("SYSTEM", (await session.Channel.ReplyAsync("query", new { window, sql = "SELECT 1 FROM dual" })).GetProperty("database").GetString());

        var running = session.Channel.Send("query", new { window, sql = "SELECT COUNT(*) FROM all_objects a, all_objects b, all_objects c" });
        await Task.Delay(1500, Ct);
        await session.Channel.ReplyAsync("cancel", new { window });
        var cancelled = (await session.Channel.AnswerAsync(running)).GetProperty("data");
        Assert.True(cancelled.GetProperty("cancelled").GetBoolean(), cancelled.ToString());
        Assert.Equal("1", (await session.Channel.ReplyAsync("query", new { window, sql = "SELECT 1 FROM dual" })).GetProperty("output")[0].GetProperty("rows")[0][0].GetString());
    }

    [Fact]
    public async Task The_explorer_lists_schemas_tables_and_columns_and_scripts_them()
    {
        oracle.SkipIfUnavailable();
        var table = $"E{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        await oracle.ExecuteAsync($"CREATE TABLE {table} (id NUMBER(10) PRIMARY KEY, name VARCHAR2(50) NOT NULL)", cancellationToken: Ct);
        await using var session = await SessionAsync();

        var server = await session.ExploreAsync(new { kind = "server" });
        Assert.Equal(["Schemas", "Security", "Management"], server.Select(Label));
        var schema = (await session.ExploreAsync(server[0])).Single(n => Label(n) == OracleFixture.UserName);
        var folders = await session.ExploreAsync(schema);
        Assert.Equal("Tables", Label(folders[0]));
        var node = (await session.ExploreAsync(folders[0])).Single(n => Label(n) == table);
        Assert.Equal(["selectTop", "create"], node.GetProperty("scripts").EnumerateArray().Select(s => s.GetString()));
        var columns = await session.ExploreAsync((await session.ExploreAsync(node))[0]);
        Assert.Equal(["ID", "NAME"], columns.Select(Label));

        Assert.Equal($"SELECT *\nFROM \"{OracleFixture.UserName}\".\"{table}\"\nWHERE ROWNUM <= 1000;\n", await session.ScriptAsync(node, "selectTop"));
        Assert.Contains($"CREATE TABLE \"{OracleFixture.UserName}\".\"{table}\"", await session.ScriptAsync(node, "create"), StringComparison.Ordinal);
        var management = await session.ExploreAsync(server[2]);
        Assert.Equal(["blocking", "report:storage", "report:backups", "report:health"], management.Select(n => n.GetProperty("tool").GetString()));
    }

    [Fact]
    public async Task The_management_reports_read_storage_backups_and_health_and_say_what_a_login_cant_read()
    {
        oracle.SkipIfUnavailable();
        await using var session = await SessionAsync();

        var info = await session.Channel.ReplyAsync("info");
        Assert.Equal(["storage", "backups", "health"], info.GetProperty("reports").EnumerateArray().Select(r => r.GetProperty("name").GetString()));
        foreach (var name in new[] { "storage", "backups", "health" })
        {
            var report = await session.Channel.ReplyAsync("report", new { name });
            var sections = report.GetProperty("sections").EnumerateArray().ToList();
            Assert.NotEmpty(sections);
            // Each section either read its view or says why it couldn't (a test login may lack SELECT_CATALOG_ROLE).
            Assert.All(sections, s => Assert.True(s.GetProperty("results").GetArrayLength() > 0 || s.GetProperty("error").GetString() is { Length: > 0 }, s.ToString()));
        }
    }

    [Fact]
    public async Task The_blocking_manager_shows_who_blocks_whom_and_kills_with_the_serial()
    {
        oracle.SkipIfUnavailable();
        var table = $"B{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        await oracle.ExecuteAsync($"CREATE TABLE {table} (id NUMBER PRIMARY KEY, balance NUMBER)", cancellationToken: Ct);
        await oracle.ExecuteAsync($"INSERT INTO {table} VALUES (1, 100)", cancellationToken: Ct);

        // SYSTEM reads V$SESSION and may kill sessions.
        await using var session = await SessionAsync("SYSTEM");
        // ODP.NET commits each statement unless it's in a transaction: the lead blocker holds one open.
        await using var lead = await oracle.OpenAsync(cancellationToken: Ct);
        await using var transaction = await lead.BeginTransactionAsync(Ct);
        await using (var begin = lead.CreateCommand())
        {
            begin.CommandText = $"UPDATE {table} SET balance = 0 WHERE id = 1";
            await begin.ExecuteNonQueryAsync(Ct);
        }

        await using var waiter = await oracle.OpenAsync(cancellationToken: Ct);
        await using var wait = waiter.CreateCommand();
        wait.CommandText = $"UPDATE {table} SET balance = 1 WHERE id = 1";
        var waiting = wait.ExecuteNonQueryAsync(Ct);
        await Task.Delay(1500, Ct);

        var report = await session.Channel.ReplyAsync("blocking");
        var chain = Assert.Single(report.GetProperty("leads").EnumerateArray());
        Assert.Equal(OracleFixture.UserName, chain.GetProperty("login").GetString());
        Assert.Equal(1, chain.GetProperty("blockedVolume").GetInt32());
        var serial = chain.GetProperty("serial").GetInt32();

        Assert.Contains("serial number", await session.Channel.ErrorAsync("kill", new { sessionId = chain.GetProperty("sessionId").GetInt32() }), StringComparison.Ordinal);
        await session.Channel.ReplyAsync("kill", new { sessionId = chain.GetProperty("sessionId").GetInt32(), serial });
        await waiting.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal("oracle.kill", Assert.Single(session.Audit.Events).Action);
    }

    private static string? Label(JsonElement node) => node.GetProperty("label").GetString();

    private FakeConnectivity Core(string password = OracleFixture.Password, string user = OracleFixture.UserName) => new(oracle.Port, user, password);

    /// <summary>A database tab, as the engine's session type opens it: db-core's handler, with this engine.</summary>
    private async Task<Session> SessionAsync(string user = OracleFixture.UserName)
    {
        var audit = new FakeAudit();
        var handler = new DatabaseSessionHandler<OracleEngine>(new OracleEngine(Core(user: user), new FakeBroker(user)), audit, TimeProvider.System);
        var stream = await handler.OpenAsync(Requests.Session(), Ct);
        var channel = new FakeChannel();
        return new Session(stream, channel, audit, stream.RunAsync(channel, Ct));
    }

    private sealed class Session(IStreamSession stream, FakeChannel channel, FakeAudit audit, Task running) : IAsyncDisposable
    {
        public FakeChannel Channel => channel;

        public FakeAudit Audit => audit;

        public async Task<string> WindowAsync() => (await channel.ReplyAsync("window.open")).GetProperty("window").GetString()!;

        public async Task<List<JsonElement>> ExploreAsync(object node) => [.. (await channel.ReplyAsync("explore", new { node })).EnumerateArray()];

        public async Task<string> ScriptAsync(JsonElement node, string action) => (await channel.ReplyAsync("script", new { node, action })).GetProperty("sql").GetString()!;

        public async ValueTask DisposeAsync()
        {
            channel.Close();
            await running;
            await stream.DisposeAsync();
        }
    }
}
