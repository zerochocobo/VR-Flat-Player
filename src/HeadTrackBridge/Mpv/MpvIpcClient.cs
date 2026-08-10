using System.Text;
using System.Text.Json;
using System.IO.Pipes;

namespace HeadTrackBridge.Mpv;

/// <summary>
/// Minimal mpv JSON IPC client over a Windows named pipe.
///
/// Only what the bridge needs: fire-and-forget commands, plus reading the event
/// stream so that (a) the pipe's read buffer never fills up and stalls mpv, and
/// (b) we can pick up <c>client-message</c> events, which is how mpv key
/// bindings reach us (see input.conf).
/// </summary>
public sealed class MpvIpcClient : IDisposable
{
    private readonly string _pipeName;
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private Task? _reader;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private readonly Dictionary<int, TaskCompletionSource<JsonElement?>> _pending = new();
    private int _nextRequestId = 1;

    public MpvIpcClient(string pipePath)
    {
        // Accept both `\\.\pipe\name` and a bare `name`.
        const string prefix = @"\\.\pipe\";
        _pipeName = pipePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? pipePath[prefix.Length..]
            : pipePath;
    }

    public bool IsConnected => _pipe?.IsConnected == true;

    /// <summary>Raised for <c>script-message</c> sent from mpv (e.g. from a key binding).</summary>
    public event Action<string[]>? ClientMessage;

    /// <summary>Raised when mpv closes the pipe — normally because the user quit mpv.</summary>
    public event Action? Disconnected;

    /// <summary>
    /// Raised on mpv's file-loaded event. Needed because the player can outlive
    /// any one file: opening a second video from uosc's menu must re-run mode
    /// detection, or it silently keeps the previous file's projection.
    /// </summary>
    public event Action? FileLoaded;

    /// <summary>
    /// Raised for every observed property change, as (name, value). Value is
    /// null when mpv reports the property as unavailable — which happens
    /// routinely between files, so subscribers must tolerate it.
    /// </summary>
    public event Action<string, JsonElement?>? PropertyChanged;

    /// <summary>
    /// Ask mpv to push changes to a property. Ids are handed out from the same
    /// counter as request ids so an observe reply can never be mistaken for a
    /// property event; we key the event on the name instead of the id anyway.
    /// </summary>
    public Task ObserveAsync(string name)
    {
        int id;
        lock (_pending) id = _nextRequestId++;
        return SendAsync("observe_property", id, name);
    }

    public async Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(250, ct).ConfigureAwait(false);
                _pipe = pipe;
                _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = false };
                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _reader = Task.Run(() => ReadLoop(_cts.Token), _cts.Token);
                return true;
            }
            catch (TimeoutException) { /* mpv not up yet */ }
            catch (OperationCanceledException) { return false; }
            catch (IOException) { await Task.Delay(100, ct).ConfigureAwait(false); }
        }
        return false;
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(_pipe!, new UTF8Encoding(false), false, 8192, leaveOpen: true);
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;
                HandleLine(line);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (IOException) { /* pipe torn down */ }
        catch (ObjectDisposedException) { return; }

        if (!ct.IsCancellationRequested) Disconnected?.Invoke();
    }

    /// <summary>What a line turned out to be. Nothing here touches subscribers.</summary>
    private readonly record struct Incoming(
        bool FileLoaded, string? ChangedProperty, JsonElement? ChangedValue, string[]? Message);

    private void HandleLine(string line)
    {
        Incoming incoming;
        try
        {
            incoming = Parse(line);
        }
        catch (JsonException)
        {
            // mpv occasionally emits non-JSON on the socket during shutdown.
            return;
        }

        // Subscribers run outside the parse, and this is worth keeping that way
        // in both directions.
        //
        // They used to run inside it, so a JsonException from any handler — a
        // config save serialising, say — was caught as if mpv had sent
        // malformed JSON, and the handler died silently mid-way. Moving them
        // out fixed that but introduced the opposite bug for a while: the parse
        // returns early for most event types, and an early return skips
        // whatever follows the try, so file-loaded and property-change stopped
        // being delivered at all. Splitting the parse into its own method is
        // what makes both mistakes impossible rather than merely fixed.
        try
        {
            if (incoming.FileLoaded) FileLoaded?.Invoke();
            if (incoming.ChangedProperty is { } prop) PropertyChanged?.Invoke(prop, incoming.ChangedValue);
            if (incoming.Message is { } msg) ClientMessage?.Invoke(msg);
        }
        catch (Exception e)
        {
            var what = incoming.Message is { Length: > 1 } m ? m[1] : incoming.ChangedProperty ?? "event";
            Console.Error.WriteLine($"\n  [ipc] handler for '{what}' threw: {e}");
        }
    }

    /// <summary>
    /// Classify one line. Completes any pending request itself — that is a
    /// promise to our own caller, not a subscriber callback.
    /// </summary>
    private Incoming Parse(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return default;

        if (!root.TryGetProperty("event", out var ev))
        {
            // A command reply. Hand it to whoever is awaiting this request id.
            if (!root.TryGetProperty("request_id", out var rid)) return default;
            TaskCompletionSource<JsonElement?>? tcs;
            lock (_pending)
            {
                if (!_pending.Remove(rid.GetInt32(), out tcs)) return default;
            }
            var ok = root.TryGetProperty("error", out var err) && err.GetString() == "success";
            // Clone: the JsonDocument is disposed when this method returns.
            tcs.TrySetResult(ok && root.TryGetProperty("data", out var d) ? d.Clone() : null);
            return default;
        }

        var name = ev.GetString();
        if (name == "file-loaded") return new Incoming(true, null, null, null);

        if (name == "property-change")
        {
            if (!root.TryGetProperty("name", out var pn)) return default;
            // Clone for the same reason as command replies.
            var value = root.TryGetProperty("data", out var pd) && pd.ValueKind != JsonValueKind.Null
                ? pd.Clone()
                : (JsonElement?)null;
            return new Incoming(false, pn.GetString() ?? "", value, null);
        }

        if (name != "client-message") return default;
        if (!root.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Array) return default;

        var list = new List<string>();
        foreach (var a in args.EnumerateArray()) list.Add(a.GetString() ?? "");
        return list.Count > 0 ? new Incoming(false, null, null, list.ToArray()) : default;
    }

    /// <summary>Send a raw command array, e.g. <c>["set_property","pause",true]</c>.</summary>
    public async Task SendAsync(params object[] command)
    {
        if (_writer is null) return;
        var payload = JsonSerializer.Serialize(new { command, request_id = 0 });
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writer.WriteAsync(payload).ConfigureAwait(false);
            await _writer.WriteAsync('\n').ConfigureAwait(false);
            await _writer.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException) { Disconnected?.Invoke(); }
        catch (ObjectDisposedException) { /* shutting down */ }
        finally { _writeLock.Release(); }
    }

    /// <summary>
    /// Reads an mpv property. Returns null if mpv reports an error (typically
    /// "property unavailable" because no file is loaded yet) or if it does not
    /// answer within <paramref name="timeoutMs"/>.
    /// </summary>
    public async Task<JsonElement?> GetPropertyAsync(string name, int timeoutMs = 2000)
    {
        if (_writer is null) return null;

        int id;
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending)
        {
            id = _nextRequestId++;
            _pending[id] = tcs;
        }

        var payload = JsonSerializer.Serialize(new { command = new object[] { "get_property", name }, request_id = id });
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writer.WriteAsync(payload).ConfigureAwait(false);
            await _writer.WriteAsync('\n').ConfigureAwait(false);
            await _writer.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            lock (_pending) _pending.Remove(id);
            return null;
        }
        finally { _writeLock.Release(); }

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        if (completed != tcs.Task)
        {
            lock (_pending) _pending.Remove(id);
            return null;
        }
        return await tcs.Task.ConfigureAwait(false);
    }

    public async Task<int?> GetIntPropertyAsync(string name)
    {
        var v = await GetPropertyAsync(name).ConfigureAwait(false);
        return v is { ValueKind: JsonValueKind.Number } e ? e.GetInt32() : null;
    }

    public async Task<string?> GetStringPropertyAsync(string name)
    {
        var v = await GetPropertyAsync(name).ConfigureAwait(false);
        return v is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;
    }

    public async Task<bool?> GetBoolPropertyAsync(string name)
    {
        var v = await GetPropertyAsync(name).ConfigureAwait(false);
        return v?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    public async Task<double?> GetDoublePropertyAsync(string name)
    {
        var v = await GetPropertyAsync(name).ConfigureAwait(false);
        return v is { ValueKind: JsonValueKind.Number } e ? e.GetDouble() : null;
    }

    public Task SetPropertyAsync(string name, object value) => SendAsync("set_property", name, value);

    /// <summary>
    /// Deliver a key to mpv as if it had been typed in its window.
    ///
    /// Needed because the host owns the keyboard: mpv's window is a cross-process
    /// child, and Windows does not hand keyboard focus to one of those, so
    /// nothing typed at the player would otherwise reach mpv or uosc.
    /// </summary>
    public Task KeyPressAsync(string mpvKeyName) => SendAsync("keypress", mpvKeyName);

    public Task ShowTextAsync(string text, int durationMs = 1200) =>
        SendAsync("show-text", text, durationMs);

    public void Dispose()
    {
        _cts?.Cancel();
        try { _reader?.Wait(TimeSpan.FromMilliseconds(200)); } catch { /* shutting down */ }

        // Disposing a StreamWriter flushes it, and the pipe is usually already
        // gone by now: quitting the player quits mpv, and mpv closes the pipe on
        // its way out. That throws out of Dispose, past Main, and prints an
        // unhandled-exception trace on a perfectly normal exit — the last thing
        // the user sees, and it reads like a crash.
        try { _writer?.Dispose(); } catch (IOException) { /* mpv closed first */ }
        try { _pipe?.Dispose(); } catch (IOException) { /* same */ }
        _cts?.Dispose();
        _writeLock.Dispose();
    }
}
