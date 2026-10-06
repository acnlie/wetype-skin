using System;
using System.Windows.Forms;

namespace WeTypeSkinStudio
{
    // Disposing the editor client never stops the independent renderer.
    internal sealed class PetWorker : IDisposable
    {
        private readonly Timer timer = new Timer { Interval = 700 };
        private readonly string diagnostics;
        private PetRuntime snapshot = new PetRuntime { State = "idle" };
        internal event Action<PetRuntime> Changed;
        internal PetWorker(bool enableDiagnostics, string diagnosticFile)
        {
            diagnostics = enableDiagnostics ? diagnosticFile : null;
            timer.Tick += delegate
            {
                PetRuntime value = PetHost.ReadStatus();
                if (value == null) return;
                snapshot = value;
                if (Changed != null) Changed(value);
            };
            timer.Start();
        }
        internal void Configure(SkinTheme theme)
        {
            ThemeStore.Save(theme, ThemeStore.CurrentPath);
            PetHost.Start(theme, diagnostics);
            snapshot = PetHost.ReadStatus() ?? new PetRuntime { State = "idle", Enabled = theme.PetEnabled };
        }
        internal void Suspend(bool value) { PetHost.Suspend(value); }
        internal PetRuntime Snapshot() { return snapshot; }
        public void Dispose() { timer.Stop(); timer.Dispose(); }
    }
}
