using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NKeeper.Abstractions;

namespace NKeeper.Plugins.Oracle.Tests;

internal sealed class FakeAudit : IPluginAudit
{
    public List<(string Action, Guid? HostId, string Detail)> Events { get; } = [];

    public Task RecordAsync(string action, Guid? hostId, string detail, CancellationToken cancellationToken = default)
    {
        lock (Events)
        {
            Events.Add((action, hostId, detail));
        }

        return Task.CompletedTask;
    }
}

/// <summary>The browser's end of a session, without a server: requests in, replies out.</summary>
internal sealed class FakeChannel : IStreamChannel
{
    private readonly Channel<StreamFrame?> _in = Channel.CreateUnbounded<StreamFrame?>();
    private readonly Channel<JsonElement> _out = Channel.CreateUnbounded<JsonElement>();
    private int _next;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async Task<StreamFrame?> ReceiveAsync(CancellationToken cancellationToken) => await _in.Reader.ReadAsync(cancellationToken);

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendTextAsync(string json, CancellationToken cancellationToken)
    {
        _out.Writer.TryWrite(JsonDocument.Parse(json).RootElement.Clone());
        return Task.CompletedTask;
    }

    /// <summary>Sends a request, returning its ID.</summary>
    public string Send(string type, object? body = null)
    {
        var id = $"r{Interlocked.Increment(ref _next)}";
        var message = JsonSerializer.SerializeToNode(body ?? new { }, JsonSerializerOptions.Web)!.AsObject();
        message["type"] = type;
        message["id"] = id;
        _in.Writer.TryWrite(new StreamFrame(true, Encoding.UTF8.GetBytes(message.ToJsonString())));
        return id;
    }

    public void Close() => _in.Writer.TryWrite(null);

    /// <summary>The reply or error to a request, skipping others' (they come back in any order).</summary>
    public async Task<JsonElement> AnswerAsync(string id)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var skipped = new List<JsonElement>();
        try
        {
            while (true)
            {
                var message = await _out.Reader.ReadAsync(timeout.Token);
                if (message.TryGetProperty("id", out var answered) && answered.GetString() == id)
                {
                    return message;
                }

                skipped.Add(message);
            }
        }
        finally
        {
            foreach (var message in skipped)
            {
                _out.Writer.TryWrite(message);
            }
        }
    }

    /// <summary>A request's reply data, failing on an error.</summary>
    public async Task<JsonElement> ReplyAsync(string type, object? body = null)
    {
        var answer = await AnswerAsync(Send(type, body));
        Assert.True(answer.GetProperty("type").GetString() == "reply", answer.ToString());
        return answer.GetProperty("data");
    }

    /// <summary>A request's error message, failing on a reply.</summary>
    public async Task<string> ErrorAsync(string type, object? body = null)
    {
        var answer = await AnswerAsync(Send(type, body));
        Assert.True(answer.GetProperty("type").GetString() == "error", answer.ToString());
        return answer.GetProperty("message").GetString()!;
    }
}
