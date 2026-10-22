using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Threading;

namespace NotchAgent
{
    /// <summary>
    /// Notification-area icon: a tiny notch tile running the same grid animation as the notch, a tooltip summary,
    /// and a dark menu with the sessions and settings. Left click jumps to the session that needs you.
    /// </summary>
    internal sealed class Tray : IDisposable
    {
        private readonly SessionStore store;
        private readonly Settings settings;
        private readonly NotchWindow notch;
        private readonly Sounds sounds;
        private readonly Action quit;
        private readonly NotifyIcon icon = new NotifyIcon();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly DispatcherTimer animation = new DispatcherTimer();
        private readonly Dictionary<string, Icon> frames = new Dictionary<string, Icon>();
        private IndicatorPattern pattern = (IndicatorPattern)(-1);
        private int head;
        private readonly int workingAnimation = Patterns.RandomAnimation();

        public Tray(SessionStore store, Settings settings, NotchWindow notch, Sounds sounds, Action quit)
        {
            this.store = store;
            this.settings = settings;
            this.notch = notch;
            this.sounds = sounds;
            this.quit = quit;

            menu.Renderer = new DarkRenderer();
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
            menu.Font = new Font("Segoe UI", 9f);
            menu.Opening += (s, e) => BuildMenu();

            icon.ContextMenuStrip = menu;
            icon.MouseClick += OnClick;
            animation.Tick += (s, e) => { head++; Draw(); };

            store.Changed += Update;
            Update();
            icon.Visible = true;

            if (!settings.TrayPromoted)
            {
                int tries = 0;
                var promote = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                promote.Tick += (s, e) => { if (PromoteOnce() || ++tries >= 10) promote.Stop(); };
                promote.Start();
            }
        }

        /// <summary>
        /// Windows 11 parks new tray icons in the overflow flyout. Show ours in the taskbar once; if the user hides it
        /// afterwards, that choice is theirs and stays. Windows 10 has no such key: nothing to do there.
        /// </summary>
        private bool PromoteOnce()
        {
            try
            {
                using (var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
                {
                    if (root == null) return Done();
                    foreach (string name in root.GetSubKeyNames())
                        using (var key = root.OpenSubKey(name, true))
                        {
                            var path = key == null ? null : key.GetValue("ExecutablePath") as string;
                            if (path == null || !path.EndsWith(@"\NotchAgent.exe", StringComparison.OrdinalIgnoreCase)) continue;
                            key.SetValue("IsPromoted", 1, Microsoft.Win32.RegistryValueKind.DWord);
                            return Done();
                        }
                }
            }
            catch (Exception ex)
            {
                Log.Write("tray promote: " + ex.Message);
                return Done();
            }
            return false; // Explorer hasn't registered the icon yet
        }

        private bool Done()
        {
            settings.TrayPromoted = true;
            settings.Save();
            return true;
        }

        private void OnClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Session target = store.Sessions.FirstOrDefault(s => s.Effective == AgentState.Awaiting && s.HostWindow != IntPtr.Zero);
            if (target != null) Native.FocusWindow(target.HostWindow);
            else notch.Peek();
        }

        // --- Icon --------------------------------------------------------------------------------

        private void Update()
        {
            IndicatorPattern next = Patterns.Of(store.Aggregate);
            if (next != pattern)
            {
                pattern = next;
                head = 0;
                animation.Stop();
                // The idle trail stays still in the tray; only activity animates.
                if (pattern != IndicatorPattern.Idle)
                {
                    animation.Interval = TimeSpan.FromMilliseconds(Patterns.StepMs(pattern));
                    animation.Start();
                }
                Draw();
            }
            icon.Text = Summary(63);
        }

        private void Draw()
        {
            int frame = pattern == IndicatorPattern.Working ? head % 12 : pattern == IndicatorPattern.Awaiting ? head % 3 : 0;
            string key = pattern + ":" + frame;
            Icon ico;
            if (!frames.TryGetValue(key, out ico))
            {
                ico = Render(pattern, frame);
                frames[key] = ico;
            }
            icon.Icon = ico;
        }

        private Icon Render(IndicatorPattern p, int frame)
        {
            int size = SystemInformation.SmallIconSize.Width;
            using (var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // A black tile, so the icon reads on both light and dark taskbars.
                float r = size * 0.22f;
                using (var tile = RoundedRect(new RectangleF(0.5f, 0.5f, size - 1, size - 1), r))
                using (var black = new SolidBrush(Color.FromArgb(255, 12, 12, 12)))
                using (var edge = new Pen(Color.FromArgb(90, 255, 255, 255), 1f))
                {
                    g.FillPath(black, tile);
                    g.DrawPath(edge, tile);
                }

                g.SmoothingMode = SmoothingMode.None;
                int cell = Math.Max(3, (int)Math.Floor(size * 0.72 / 3));
                int origin = (size - cell * 3) / 2;
                System.Windows.Media.Color c = Patterns.ColorOf(p);
                for (int i = 0; i < 9; i++)
                {
                    double o = Patterns.Opacity(i, p, frame, workingAnimation);
                    if (p == IndicatorPattern.Idle) o = i == 4 ? 1 : 0.18; // a lit centre dot, like the macOS idle mask
                    if (o <= 0) o = 0.10;
                    using (var b = new SolidBrush(Color.FromArgb((int)(o * 255), c.R, c.G, c.B)))
                        g.FillRectangle(b, origin + (i % 3) * cell, origin + (i / 3) * cell, cell, cell);
                }

                IntPtr h = bmp.GetHicon();
                Icon result = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return result;
            }
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private string Summary(int max)
        {
            int n = store.Sessions.Count;
            int waiting = store.Sessions.Count(s => s.Effective == AgentState.Awaiting);
            int working = store.Sessions.Count(s => s.Effective == AgentState.Working);
            string text = n == 0 ? "NotchAgent · aucune session"
                : "NotchAgent · " + n + (n > 1 ? " sessions" : " session")
                  + (waiting > 0 ? " · " + waiting + " à valider" : "")
                  + (working > 0 ? " · " + working + " en cours" : "");
            return text.Length > max ? text.Substring(0, max) : text;
        }

        // --- Menu --------------------------------------------------------------------------------

        private void BuildMenu()
        {
            menu.Items.Clear();
            menu.Items.Add(new ToolStripLabel("NotchAgent") { Font = new Font(menu.Font, FontStyle.Bold) });
            menu.Items.Add(new ToolStripLabel(Summary(120).Replace("NotchAgent · ", "")) { ForeColor = Color.FromArgb(150, 150, 150) });

            if (store.Sessions.Count > 0)
            {
                menu.Items.Add(new ToolStripSeparator());
                foreach (Session s in store.Sessions.OrderBy(x => x.StartedAt))
                {
                    string activity, info;
                    NotchWindow.Describe(s, out activity, out info);
                    Session target = s;
                    var item = new ToolStripMenuItem(s.Project + "  —  " + activity, null, (o, e) => Native.FocusWindow(target.HostWindow));
                    item.Enabled = s.HostWindow != IntPtr.Zero;
                    item.ToolTipText = s.Cwd;
                    menu.Items.Add(item);
                }
            }

            menu.Items.Add(new ToolStripSeparator());

            var display = new ToolStripMenuItem("Écran");
            display.DropDownItems.Add(Choice("Écran principal", settings.Display == "primary", () => settings.Display = "primary"));
            display.DropDownItems.Add(Choice("Écran de la fenêtre active", settings.Display == "active", () => settings.Display = "active"));
            Screen[] screens = Screen.AllScreens;
            if (screens.Length > 1)
            {
                display.DropDownItems.Add(new ToolStripSeparator());
                for (int i = 0; i < screens.Length; i++)
                {
                    Screen sc = screens[i];
                    string label = "Écran " + (i + 1) + "  ·  " + sc.Bounds.Width + " × " + sc.Bounds.Height + (sc.Primary ? "  (principal)" : "");
                    display.DropDownItems.Add(Choice(label, settings.Display == sc.DeviceName, () => settings.Display = sc.DeviceName));
                }
            }
            Recolor(display);
            menu.Items.Add(display);

            var sound = new ToolStripMenuItem("Sons");
            sound.DropDownItems.Add(Toggle("Activer les sons", settings.SoundEnabled, v => settings.SoundEnabled = v));
            var volume = new ToolStripMenuItem("Volume");
            foreach (int pct in new[] { 15, 35, 60, 100 })
            {
                double v = pct / 100.0;
                volume.DropDownItems.Add(Choice(pct + " %", Math.Abs(settings.SoundVolume - v) < 0.01, () => { settings.SoundVolume = v; sounds.Preview(settings.AttentionSound); }));
            }
            sound.DropDownItems.Add(volume);
            sound.DropDownItems.Add(SoundPicker("Son d'alerte (validation)", settings.AttentionSound, f => settings.AttentionSound = f));
            sound.DropDownItems.Add(SoundPicker("Son de fin de tâche", settings.FinishSound, f => settings.FinishSound = f));
            Recolor(sound);
            menu.Items.Add(sound);

            menu.Items.Add(Toggle("Pastille au repos", settings.ShowIdlePill, v => settings.ShowIdlePill = v));
            menu.Items.Add(Toggle("Masquer en plein écran", settings.HideInFullscreen, v => settings.HideInFullscreen = v));
            menu.Items.Add(Toggle("Rester ouvert sans Claude Code", settings.StayOpen, v => settings.StayOpen = v));

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Ouvrir le journal", null, (o, e) =>
            {
                try { Process.Start(new ProcessStartInfo(Paths.LogFile) { UseShellExecute = true }); }
                catch (Exception ex) { Log.Write("open log: " + ex.Message); }
            }));
            menu.Items.Add(new ToolStripMenuItem("Quitter NotchAgent", null, (o, e) => quit()));
            Recolor(menu.Items);
        }

        private ToolStripMenuItem SoundPicker(string label, string current, Action<string> set)
        {
            var item = new ToolStripMenuItem(label);
            foreach (string file in Settings.Sounds)
            {
                string f = file;
                item.DropDownItems.Add(Choice(f.Replace(".wav", "").Replace("Windows ", ""), current == f, () => { set(f); sounds.Preview(f); }));
            }
            Recolor(item);
            return item;
        }

        private ToolStripMenuItem Choice(string label, bool selected, Action apply)
        {
            return new ToolStripMenuItem(label, null, (o, e) => { apply(); Changed(); }) { Checked = selected };
        }

        private ToolStripMenuItem Toggle(string label, bool value, Action<bool> set)
        {
            return new ToolStripMenuItem(label, null, (o, e) => { set(!value); Changed(); }) { Checked = value };
        }

        private void Changed()
        {
            settings.Save();
            notch.Place();
            notch.Refresh();
        }

        private static void Recolor(ToolStripMenuItem item)
        {
            item.DropDown.BackColor = DarkRenderer.Back;
            Recolor(item.DropDownItems);
        }

        private static void Recolor(ToolStripItemCollection items)
        {
            foreach (ToolStripItem i in items)
                if (i.ForeColor != Color.FromArgb(150, 150, 150)) i.ForeColor = Color.FromArgb(235, 235, 235);
        }

        public void Dispose()
        {
            animation.Stop();
            icon.Visible = false;
            icon.Dispose();
            menu.Dispose();
            foreach (Icon i in frames.Values) i.Dispose();
        }

        /// <summary>Windows 11-like dark menu.</summary>
        private sealed class DarkRenderer : ToolStripProfessionalRenderer
        {
            public static readonly Color Back = Color.FromArgb(32, 32, 32);

            public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = Color.FromArgb(200, 200, 200);
                base.OnRenderArrow(e);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                Rectangle r = e.ImageRectangle;
                using (var pen = new Pen(Color.FromArgb(240, 240, 240), 1.6f))
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
                    e.Graphics.DrawLines(pen, new[] { new PointF(cx - 4, cy), new PointF(cx - 1.2f, cy + 3), new PointF(cx + 4.5f, cy - 3.5f) });
                }
            }
        }

        private sealed class DarkColors : ProfessionalColorTable
        {
            private static readonly Color Back = DarkRenderer.Back;
            private static readonly Color Hover = Color.FromArgb(55, 55, 55);
            private static readonly Color Line = Color.FromArgb(60, 60, 60);

            public override Color ToolStripDropDownBackground { get { return Back; } }
            public override Color ImageMarginGradientBegin { get { return Back; } }
            public override Color ImageMarginGradientMiddle { get { return Back; } }
            public override Color ImageMarginGradientEnd { get { return Back; } }
            public override Color MenuBorder { get { return Line; } }
            public override Color MenuItemBorder { get { return Hover; } }
            public override Color MenuItemSelected { get { return Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
            public override Color MenuItemPressedGradientBegin { get { return Hover; } }
            public override Color MenuItemPressedGradientEnd { get { return Hover; } }
            public override Color SeparatorDark { get { return Line; } }
            public override Color SeparatorLight { get { return Back; } }
            public override Color CheckBackground { get { return Back; } }
            public override Color CheckSelectedBackground { get { return Hover; } }
            public override Color CheckPressedBackground { get { return Hover; } }
        }
    }
}
