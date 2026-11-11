using System;

namespace GraphiteCompanion.Services
{
    /// <summary>Owns endpoint mute only while enabled; restores the previous value when detached.</summary>
    public sealed class PushToTalkController : IDisposable
    {
        private readonly Action<bool> write;
        private readonly bool originalMute;
        private bool disposed;
        public PushToTalkController(Func<bool> read, Action<bool> write)
        {
            this.write = write;
            originalMute = read();
            write(true);
        }
        public void SetHeld(bool held) { if (!disposed) write(!held); }
        public void Dispose()
        {
            if (disposed) return;
            write(originalMute);
            disposed = true;
        }
    }
}
