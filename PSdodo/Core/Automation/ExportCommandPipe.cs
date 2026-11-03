using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace PsExporter.Core.Automation;

/// <summary>One bounded request per connection. A reply means the main instance accepted it.</summary>
internal static class ExportCommandPipe
{
    private const int MaxPayloadBytes = 4096;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    internal static async Task<bool> SendAsync(string name, string[] args)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
        try
        {
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var payload = JsonSerializer.SerializeToUtf8Bytes(args);
            if (payload.Length > MaxPayloadBytes) return false;
            await client.WriteAsync(BitConverter.GetBytes(payload.Length), timeout.Token).ConfigureAwait(false);
            await client.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
            var reply = new byte[1];
            await client.ReadExactlyAsync(reply, timeout.Token).ConfigureAwait(false);
            return reply[0] == 1;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static async Task ListenAsync(string name,
        Func<ExportInvocation, CancellationToken, Task<bool>> accept, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(stop).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(RequestTimeout);
                var length = new byte[sizeof(int)];
                await server.ReadExactlyAsync(length, timeout.Token).ConfigureAwait(false);
                var count = BitConverter.ToInt32(length);
                if (count is <= 0 or > MaxPayloadBytes) continue;
                var payload = new byte[count];
                await server.ReadExactlyAsync(payload, timeout.Token).ConfigureAwait(false);
                var args = JsonSerializer.Deserialize<string[]>(payload);
                var accepted = args is not null && ExportInvocation.TryParse(args, out var invocation)
                    && invocation is not null && await accept(invocation, timeout.Token).ConfigureAwait(false);
                await server.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException or JsonException)
            {
                if (stop.IsCancellationRequested) break;
                try { await Task.Delay(150, stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
