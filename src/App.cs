using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace NotchAgent
{
    /// <summary>Wires the pieces together and decides when the app leaves.</summary>
    internal sealed class AppCore
    {
        private readonly Application app;
        private Settings settings;
        private SessionStore store;
        private NotchWindow notch;
        private Tray tray;
        private Sounds sounds;
        private PipeServer pipe;
        private DispatcherTimer life;
        private readonly DateTime startedAt = DateTime.Now;
        private DateTime emptySince = DateTime.MaxValue;
        private bool quitting;

        public AppCore(Application app) { this.app = app; }

        public void Start()
        {
            Dispatcher ui = app.Dispatcher;
            settings = Settings.Load();
            sounds = new Sounds(settings);
            store = new SessionStore(ui);
            notch = new NotchWindow(store, settings);
            notch.Show();
            tray = new Tray(store, settings, notch, sounds, Quit);

            store.Finished += s =>
            {
                // Only celebrate when nothing else is still running.
                if (store.Aggregate == AgentState.Idle) notch.Flash(IndicatorPattern.Done, "Terminé", s.Project, 2.6);
                sounds.Finish();
            };
            store.Failed += s =>
            {
                notch.Flash(IndicatorPattern.Error, "Erreur", s.Project, 4);
                sounds.Attention();
            };
            store.NeedsAttention += s => sounds.Attention();
            store.Restore();

            pipe = new PipeServer(line => ui.BeginInvoke(new Action(() => Handle(line))));
            pipe.Start();

            life = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            life.Tick += (s, e) => CheckLife();
            life.Start();
            Log.Write("started (pid " + System.Diagnostics.Process.GetCurrentProcess().Id + ")");
        }

        private void Handle(string line)
        {
            try
            {
                var msg = Json.ParseObject(line);
                if (msg == null) return;
                if (Json.Str(msg, "event") == "__quit") { Quit(); return; }
                store.Apply(msg);
            }
            catch (Exception ex)
            {
                Log.Write("message: " + ex.Message);
            }
        }

        /// <summary>The notch exists while Claude Code does: once the last session is gone (and stays gone), leave.</summary>
        private void CheckLife()
        {
            foreach (Session stale in store.Sessions.Where(s => s.ClaudePid == 0 && (DateTime.Now - s.StateSince).TotalMinutes > 30).ToList())
                store.Remove(stale.Id);

            if (settings.StayOpen || store.Sessions.Count > 0)
            {
                emptySince = DateTime.MaxValue;
                return;
            }
            if ((DateTime.Now - startedAt).TotalSeconds < 20) return; // give the launching hook time to deliver
            if (emptySince == DateTime.MaxValue) emptySince = DateTime.Now;
            if ((DateTime.Now - emptySince).TotalSeconds >= 5) Quit();
        }

        private void Quit()
        {
            if (quitting) return;
            quitting = true;
            Log.Write("quitting");
            pipe.Stop();
            tray.Dispose();
            notch.Leave(() => app.Shutdown());
        }
    }

    internal sealed class Sounds
    {
        private readonly Settings settings;
        private readonly MediaPlayer player = new MediaPlayer();
        private DateTime last = DateTime.MinValue;

        public Sounds(Settings settings) { this.settings = settings; }

        public void Attention() { Play(settings.AttentionSound, false); }
        public void Finish() { Play(settings.FinishSound, false); }
        public void Preview(string file) { Play(file, true); }

        private void Play(string file, bool force)
        {
            if (!force && !settings.SoundEnabled) return;
            // Several sessions finishing together should chime once.
            if (!force && (DateTime.Now - last).TotalMilliseconds < 1500) return;
            last = DateTime.Now;
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", file);
                if (!File.Exists(path)) return;
                player.Open(new Uri(path));
                player.Volume = settings.SoundVolume;
                player.Play();
            }
            catch (Exception ex)
            {
                Log.Write("sound: " + ex.Message);
            }
        }
    }

    /// <summary>Receives one JSON line per hook event on \\.\pipe\NotchAgent.&lt;user&gt;, writable by the current user only.</summary>
    internal sealed class PipeServer
    {
        private const int Listeners = 4;
        private readonly Action<string> onLine;
        private volatile bool running;

        public PipeServer(Action<string> onLine) { this.onLine = onLine; }

        public void Start()
        {
            running = true;
            for (int i = 0; i < Listeners; i++)
                new Thread(Listen) { IsBackground = true, Name = "pipe-" + i }.Start();
        }

        public void Stop() { running = false; }

        private static PipeSecurity OwnerOnly()
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            return security;
        }

        private void Listen()
        {
            while (running)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(Paths.PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096, OwnerOnly()))
                    {
                        server.WaitForConnection();
                        using (var reader = new StreamReader(server, new UTF8Encoding(false)))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                                if (line.Length > 0 && running) onLine(line);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!running) return;
                    Log.Write("pipe: " + ex.Message);
                    Thread.Sleep(250);
                }
            }
        }
    }
}
