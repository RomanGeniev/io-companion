using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GraphiteCompanion.Services
{
    /// <summary>
    /// Shows "SoundShot · Игры" in the Discord profile via Discord's local RPC pipe
    /// (\\.\pipe\discord-ipc-N). Needs an Application ID from discord.com/developers;
    /// nothing is sent anywhere except to the Discord app running on this PC.
    /// </summary>
    public sealed class DiscordPresence : IDisposable
    {
        private readonly string appId;
        private NamedPipeClientStream? pipe;
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly long startedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private string? lastPayload;

        public DiscordPresence(string appId) => this.appId = appId;

        public bool Connected => pipe?.IsConnected == true;

        /// <summary>Updates the status; silently does nothing when Discord isn't running.</summary>
        public async Task UpdateAsync(string details, string state)
        {
            await gate.WaitAsync();
            try
            {
                if (!await EnsureConnectedAsync()) return;
                var activity = new
                {
                    cmd = "SET_ACTIVITY",
                    nonce = Guid.NewGuid().ToString(),
                    args = new
                    {
                        pid = Environment.ProcessId,
                        activity = new
                        {
                            details,
                            state,
                            timestamps = new { start = startedAt },
                            assets = new { large_image = "logo", large_text = "SoundShot" },
                        },
                    },
                };
                string key = details + "|" + state;
                if (key == lastPayload) return;
                await SendAsync(1, JsonSerializer.Serialize(activity));
                await ReadFrameAsync(); // consume Discord's reply so the pipe never fills up
                lastPayload = key;
            }
            catch
            {
                Drop();
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task ClearAsync()
        {
            await gate.WaitAsync();
            try
            {
                if (!Connected) return;
                await SendAsync(1, JsonSerializer.Serialize(new
                {
                    cmd = "SET_ACTIVITY", nonce = Guid.NewGuid().ToString(),
                    args = new { pid = Environment.ProcessId },
                }));
            }
            catch { }
            finally
            {
                lastPayload = null;
                gate.Release();
            }
        }

        private async Task<bool> EnsureConnectedAsync()
        {
            if (Connected) return true;
            Drop();
            for (int i = 0; i < 10; i++)
            {
                var p = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await p.ConnectAsync(150);
                    pipe = p;
                    await SendAsync(0, JsonSerializer.Serialize(new { v = 1, client_id = appId }));
                    await ReadFrameAsync(); // READY (or an error for a wrong Application ID)
                    lastPayload = null;
                    return true;
                }
                catch
                {
                    p.Dispose();
                    pipe = null;
                }
            }
            return false;
        }

        private async Task SendAsync(int op, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] frame = new byte[8 + body.Length];
            BitConverter.GetBytes(op).CopyTo(frame, 0);
            BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
            body.CopyTo(frame, 8);
            await pipe!.WriteAsync(frame);
            await pipe.FlushAsync();
        }

        private async Task ReadFrameAsync()
        {
            var header = new byte[8];
            using var cts = new CancellationTokenSource(2000);
            await pipe!.ReadExactlyAsync(header, cts.Token);
            int len = BitConverter.ToInt32(header, 4);
            if (len is < 0 or > 1 << 20) throw new InvalidDataException();
            var body = new byte[len];
            await pipe.ReadExactlyAsync(body, cts.Token);
            if (BitConverter.ToInt32(header, 0) == 2) throw new IOException("Discord closed the connection"); // wrong app id
        }

        private void Drop()
        {
            try { pipe?.Dispose(); } catch { }
            pipe = null;
        }

        public void Dispose() => Drop();
    }
}
