using System.Diagnostics;
using System.Text.Json;

namespace CodexTray.Core;

public sealed class CodexAppServerClient : IDisposable
{
    private readonly string _codexPath;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly object _processLock = new();
    private readonly object _stderrLock = new();
    private readonly Queue<string> _stderrLines = new();
    private Process? _process;
    private int _nextRequestId;
    private bool _disposed;

    public CodexAppServerClient(string codexPath, TimeSpan? timeout = null)
    {
        _codexPath = Path.GetFullPath(codexPath);
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
    }

    public async Task<CodexSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        await _requestLock.WaitAsync(cancellationToken);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);

            try
            {
                var process = await GetOrStartProcessAsync(timeout.Token);
                var accountId = NextRequestId();
                var rateLimitsId = NextRequestId();
                var usageId = NextRequestId();

                await SendAsync(process.StandardInput, new
                {
                    method = "account/read",
                    id = accountId,
                    @params = new { refreshToken = false }
                }, timeout.Token);
                await SendAsync(process.StandardInput, new
                {
                    method = "account/rateLimits/read",
                    id = rateLimitsId,
                    @params = new { }
                }, timeout.Token);
                await SendAsync(process.StandardInput, new
                {
                    method = "account/usage/read",
                    id = usageId,
                    @params = new { }
                }, timeout.Token);

                var responses = await ReadResponsesAsync(
                    process.StandardOutput,
                    [accountId, rateLimitsId, usageId],
                    timeout.Token);
                return CodexSnapshotParser.Parse(
                    responses.GetValueOrDefault(accountId),
                    responses[rateLimitsId],
                    responses.GetValueOrDefault(usageId),
                    DateTimeOffset.Now);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                StopProcess();
                throw new TimeoutException($"Codex app-server did not answer within {_timeout.TotalSeconds:0} seconds.");
            }
            catch (OperationCanceledException)
            {
                StopProcess();
                throw;
            }
            catch (Exception ex)
            {
                var detail = GetStderrDetail();
                StopProcess();
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(detail) ? ex.Message : $"{ex.Message} ({detail})",
                    ex);
            }
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopProcess();
        GC.SuppressFinalize(this);
    }

    private async Task<Process> GetOrStartProcessAsync(CancellationToken cancellationToken)
    {
        Process? current;
        lock (_processLock)
        {
            current = _process;
        }

        if (current is not null && !HasExited(current))
        {
            return current;
        }

        StopProcess();
        var process = StartProcess();
        lock (_stderrLock)
        {
            _stderrLines.Clear();
        }
        lock (_processLock)
        {
            _process = process;
        }
        _ = PumpStderrAsync(process.StandardError);

        var initializeId = NextRequestId();
        await SendAsync(process.StandardInput, new
        {
            method = "initialize",
            id = initializeId,
            @params = new
            {
                clientInfo = new
                {
                    name = "CodexTray",
                    title = "Codex Tray",
                    version = typeof(CodexAppServerClient).Assembly.GetName().Version?.ToString(3) ?? "unknown"
                }
            }
        }, cancellationToken);

        await ReadResponsesAsync(process.StandardOutput, [initializeId], cancellationToken);
        await SendAsync(process.StandardInput, new { method = "initialized", @params = new { } }, cancellationToken);
        return process;
    }

    private Process StartProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _codexPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add("stdio://");
        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("Could not start the Codex app-server.");
    }

    private static async Task SendAsync(
        StreamWriter writer,
        object message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await writer.WriteLineAsync(JsonSerializer.Serialize(message));
        await writer.FlushAsync(cancellationToken);
    }

    private static async Task<Dictionary<int, JsonElement>> ReadResponsesAsync(
        StreamReader reader,
        IReadOnlyCollection<int> expectedIds,
        CancellationToken cancellationToken)
    {
        var responses = new Dictionary<int, JsonElement>();
        while (expectedIds.Any(id => !responses.ContainsKey(id)))
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                throw new InvalidOperationException("Codex app-server closed its output unexpectedly.");
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var idProperty)
                    && idProperty.TryGetInt32(out var id)
                    && expectedIds.Contains(id))
                {
                    responses[id] = root.Clone();
                }
            }
            catch (JsonException)
            {
                // Ignore non-protocol noise and keep waiting for the requested ids.
            }
        }

        return responses;
    }

    private async Task PumpStderrAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                lock (_stderrLock)
                {
                    _stderrLines.Enqueue(line);
                    while (_stderrLines.Count > 50)
                    {
                        _stderrLines.Dequeue();
                    }
                }
            }
        }
        catch
        {
            // The stream closes when the app-server stops.
        }
    }

    private string GetStderrDetail()
    {
        lock (_stderrLock)
        {
            return FirstUsefulLine(string.Join(Environment.NewLine, _stderrLines));
        }
    }

    private int NextRequestId() => Interlocked.Increment(ref _nextRequestId);

    private static string FirstUsefulLine(string value)
    {
        return value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => !line.Contains("plugin", StringComparison.OrdinalIgnoreCase))
            ?? string.Empty;
    }

    private void StopProcess()
    {
        Process? process;
        lock (_processLock)
        {
            process = _process;
            _process = null;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch
        {
            // Best-effort child cleanup.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }
}
