using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Screen = System.Windows.Forms.Screen;

namespace Minou
{
    /// <summary>
    /// The notch: a borderless, click-through, always-on-top window pinned to the top centre of a monitor.
    /// Four modes, all reached through springs on width, height and both corner radii:
    ///   Pill     — thin tab while every session is idle
    ///   Bar      — indicator + activity + session count while Claude works or waits for you
    ///   Expanded — on hover: one row per session; click a row to bring its terminal forward
    ///   Hidden   — retracted into the top edge (full-screen app, or no pill wanted)
    /// </summary>
    internal sealed class NotchWindow : Window
    {
        // Window canvas in DIPs: big enough for the expanded panel. Outside the shape every pixel is fully
        // transparent, which a layered window lets clicks fall through.
        private const double CanvasW = 520, CanvasH = 480;
        private const double BarH = 34;
        private const double PillW = 136, PillH = 7;
        private const double ExpandedW = 460;
        private const double RowH = 58, ListPad = 8;
        private const int MaxRows = 5;

        private enum Mode { Hidden, Pill, Bar, Expanded }

        private readonly SessionStore store;
        private readonly Settings settings;

        private readonly Spring w = new Spring(PillW, 0.5, 0.82);
        private readonly Spring h = new Spring(0, 0.5, 0.82);
        private readonly Spring outer = new Spring(0, 0.5, 0.9);
        private readonly Spring inner = new Spring(0, 0.5, 0.9);
        private readonly Spring barAlpha = new Spring(0, 0.3, 1);
        private readonly Spring listAlpha = new Spring(0, 0.3, 1);
        private Spring[] springs;

        private readonly Grid root = new Grid();
        private readonly Grid island = new Grid();
        private readonly Path fill = new Path { Fill = Brushes.Black };
        private readonly Path outline = new Path { StrokeThickness = 1, Stroke = Frozen(Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2B)) };
        private readonly Grid bar = new Grid();
        private readonly GridIndicator barIndicator = new GridIndicator();
        private readonly TextBlock title = new TextBlock();
        private readonly TextBlock detail = new TextBlock();
        private readonly TextBlock count = new TextBlock();
        private readonly Grid listHost = new Grid();
        private readonly StackPanel rows = new StackPanel();
        private readonly Dictionary<string, SessionRow> rowCache = new Dictionary<string, SessionRow>();

        private readonly DispatcherTimer hoverTimer;
        private readonly DispatcherTimer housekeeping;
        private readonly DispatcherTimer flashTimer;
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private double lastFrame;
        private bool animating;

        private IntPtr hwnd;
        private Mode mode = Mode.Hidden;
        private bool expanded;
        private DateTime hoverEnter = DateTime.MaxValue, hoverLeave = DateTime.MaxValue;
        private bool clickThrough = true;
        private bool fullscreenHidden;
        private bool leaving;
        private IndicatorPattern? flashPattern;
        private string flashTitle, flashDetail;
        private Rect placedOn = Rect.Empty;
        private int housekeepingTicks;

        public NotchWindow(SessionStore store, Settings settings)
        {
            this.store = store;
            this.settings = settings;
            springs = new[] { w, h, outer, inner, barAlpha, listAlpha };

            Title = "Minou";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Width = CanvasW;
            Height = CanvasH;
            Left = -32000;
            Top = -32000;
            UseLayoutRounding = true;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            BuildVisualTree();

            hoverTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
            hoverTimer.Tick += (s, e) => TrackHover();
            housekeeping = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            housekeeping.Tick += (s, e) => Housekeep();
            flashTimer = new DispatcherTimer();
            flashTimer.Tick += (s, e) => { flashTimer.Stop(); flashPattern = null; Refresh(); };

            SourceInitialized += OnSourceInitialized;
            DpiChanged += (s, e) => Dispatcher.BeginInvoke(new Action(() => { placedOn = Rect.Empty; Place(); }));
            store.Changed += Refresh;
        }

        // --- Visual tree -------------------------------------------------------------------------

        private void BuildVisualTree()
        {
            island.HorizontalAlignment = HorizontalAlignment.Center;
            island.VerticalAlignment = VerticalAlignment.Top;
            island.Children.Add(fill);
            island.Children.Add(outline);

            // Bar: indicator | title + detail | session count
            bar.Height = BarH;
            bar.VerticalAlignment = VerticalAlignment.Top;
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(barIndicator, 0);
            bar.Children.Add(barIndicator);

            title.Foreground = Brushes.White;
            title.FontSize = 12.5;
            title.FontWeight = FontWeights.SemiBold;
            title.VerticalAlignment = VerticalAlignment.Center;
            detail.Foreground = Frozen(Color.FromRgb(0x8E, 0x8E, 0x8E));
            detail.FontSize = 12.5;
            detail.Margin = new Thickness(7, 0, 0, 0);
            detail.TextTrimming = TextTrimming.CharacterEllipsis;
            detail.VerticalAlignment = VerticalAlignment.Center;
            var text = new DockPanel { Margin = new Thickness(6, 0, 10, 1), LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(title, Dock.Left);
            text.Children.Add(title);
            text.Children.Add(detail);
            Grid.SetColumn(text, 1);
            bar.Children.Add(text);

            count.Foreground = Brushes.White;
            count.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            count.FontSize = 13;
            count.FontWeight = FontWeights.SemiBold;
            count.MinWidth = 18;
            count.TextAlignment = TextAlignment.Center;
            count.VerticalAlignment = VerticalAlignment.Center;
            count.Margin = new Thickness(0, 0, 7, 1);
            Grid.SetColumn(count, 2);
            bar.Children.Add(count);
            island.Children.Add(bar);

            // Session list under the bar
            rows.Margin = new Thickness(ListPad, ListPad, ListPad, ListPad);
            var scroller = new ScrollViewer
            {
                Content = rows,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
            };
            listHost.Margin = new Thickness(0, BarH, 0, 0);
            listHost.Children.Add(scroller);
            island.Children.Add(listHost);

            root.Children.Add(island);
            Content = root;
            ApplyFrame();
        }

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        // --- Window plumbing ---------------------------------------------------------------------

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT);
            Place();
            Refresh();
            hoverTimer.Start();
            housekeeping.Start();
        }

        private void SetClickThrough(bool value)
        {
            if (clickThrough == value || hwnd == IntPtr.Zero) return;
            clickThrough = value;
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            ex = value ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT;
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex);
        }

        private Screen TargetScreen()
        {
            if (settings.Display == "active")
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (fg != IntPtr.Zero && fg != hwnd) return Screen.FromHandle(fg);
                return Screen.PrimaryScreen;
            }
            return Screen.AllScreens.FirstOrDefault(s => s.DeviceName == settings.Display) ?? Screen.PrimaryScreen;
        }

        /// <summary>Centres the canvas on the top edge of the target monitor, in physical pixels at that monitor's DPI.</summary>
        public void Place()
        {
            if (hwnd == IntPtr.Zero) return;
            System.Drawing.Rectangle b = TargetScreen().Bounds;
            var key = new Rect(b.X, b.Y, b.Width, b.Height);
            if (key == placedOn) return;

            uint dpi = 96, dpiY;
            var center = new Native.POINT { X = b.X + b.Width / 2, Y = b.Y + 1 };
            IntPtr mon = Native.MonitorFromPoint(center, Native.MONITOR_DEFAULTTONEAREST);
            try { if (Native.GetDpiForMonitor(mon, Native.MDT_EFFECTIVE_DPI, out dpi, out dpiY) != 0) dpi = 96; }
            catch (DllNotFoundException) { dpi = 96; }
            double scale = dpi / 96.0;

            int pw = (int)Math.Round(CanvasW * scale), ph = (int)Math.Round(CanvasH * scale);
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, b.X + (b.Width - pw) / 2, b.Y, pw, ph, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            placedOn = key;
        }

        private System.Drawing.Rectangle CurrentBounds()
        {
            return hwnd == IntPtr.Zero ? Screen.PrimaryScreen.Bounds : Screen.FromHandle(hwnd).Bounds;
        }

        // --- State → targets ---------------------------------------------------------------------

        public void Flash(IndicatorPattern pattern, string flashTitleText, string flashDetailText, double seconds)
        {
            flashPattern = pattern;
            flashTitle = flashTitleText;
            flashDetail = flashDetailText;
            flashTimer.Stop();
            flashTimer.Interval = TimeSpan.FromSeconds(seconds);
            flashTimer.Start();
            Refresh();
        }

        /// <summary>Retracts into the top edge, then calls <paramref name="done"/>.</summary>
        public void Leave(Action done)
        {
            leaving = true;
            expanded = false;
            SetClickThrough(true);
            Refresh();
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            t.Tick += (s, e) => { t.Stop(); done(); };
            t.Start();
        }

        public void Refresh()
        {
            AgentState agg = store.Aggregate;
            Mode next;
            if (fullscreenHidden || leaving) next = Mode.Hidden;
            else if (expanded) next = Mode.Expanded;
            else if (flashPattern.HasValue || agg != AgentState.Idle) next = Mode.Bar;
            else if (settings.ShowIdlePill) next = Mode.Pill;
            else next = Mode.Hidden;

            UpdateBarContent(agg);
            if (next == Mode.Expanded) UpdateRows(mode != Mode.Expanded);
            mode = next;

            switch (mode)
            {
                case Mode.Hidden:
                    Target(PillW * 0.6, 0, 0, 0, 0, 0);
                    break;
                case Mode.Pill:
                    Target(PillW, PillH, 4, 3.5, 0, 0);
                    break;
                case Mode.Bar:
                    Target(BarWidth(), BarH, 8, 11, 1, 0);
                    break;
                case Mode.Expanded:
                    int n = Math.Max(1, Math.Min(MaxRows, store.Sessions.Count));
                    Target(ExpandedW, BarH + ListPad * 2 + n * RowH, 0, 18, 1, 1);
                    break;
            }
        }

        private void Target(double width, double height, double outerR, double innerR, double barOpacity, double listOpacity)
        {
            w.Target = width;
            h.Target = height;
            outer.Target = outerR;
            inner.Target = innerR;
            barAlpha.Target = barOpacity;
            listAlpha.Target = listOpacity;
            StartAnimation();
        }

        private void UpdateBarContent(AgentState agg)
        {
            int n = store.Sessions.Count;
            count.Text = n > 0 ? n.ToString() : "";

            if (flashPattern.HasValue)
            {
                barIndicator.Pattern = flashPattern.Value;
                title.Text = flashTitle;
                detail.Text = flashDetail ?? "";
                return;
            }

            Session focus = store.Focus;
            barIndicator.Pattern = Patterns.Of(agg);
            if (focus == null)
            {
                title.Text = "Claude Code";
                detail.Text = "";
                return;
            }

            string activity, info;
            Describe(focus, out activity, out info);
            title.Text = activity;
            detail.Text = info;
        }

        /// <summary>Short activity ("Terminal", "Validation requise") and its context ("npm run build", project name).</summary>
        internal static void Describe(Session s, out string activity, out string info)
        {
            string label = ToolDetail.Label(s.Tool);
            switch (s.Effective)
            {
                case AgentState.Awaiting:
                    activity = "Validation requise";
                    info = label != null ? s.Project + " · " + label : s.Project;
                    break;
                case AgentState.Working:
                    if (s.State == AgentState.Idle)
                    {
                        activity = s.Subagents.Count > 1 ? s.Subagents.Count + " agents en arrière-plan" : "Agent en arrière-plan";
                        info = s.Project;
                    }
                    else
                    {
                        activity = label ?? "Réflexion…";
                        info = s.Detail ?? s.Project;
                    }
                    break;
                default:
                    activity = "En attente";
                    info = s.Project;
                    break;
            }
        }

        private double BarWidth()
        {
            title.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var probe = new TextBlock { Text = detail.Text, FontSize = detail.FontSize, FontFamily = FontFamily };
            TextOptions.SetTextFormattingMode(probe, TextFormattingMode.Display);
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double content = 32 + 6 + title.DesiredSize.Width + (detail.Text.Length > 0 ? 7 + probe.DesiredSize.Width : 0) + 10 + 18 + 7;
            return Math.Max(232, Math.Min(400, content + 2 * 8 + 12));
        }

        // --- Session rows ------------------------------------------------------------------------

        private void UpdateRows(bool entering)
        {
            var live = new HashSet<string>(store.Sessions.Select(s => s.Id));
            foreach (string gone in rowCache.Keys.Where(k => !live.Contains(k)).ToList()) rowCache.Remove(gone);

            rows.Children.Clear();
            var ordered = store.Sessions
                .OrderBy(s => s.Effective == AgentState.Awaiting ? 0 : s.Effective == AgentState.Working ? 1 : 2)
                .ThenBy(s => s.StartedAt)
                .ToList();

            if (ordered.Count == 0)
            {
                rows.Children.Add(SessionRow.Placeholder());
            }
            foreach (Session s in ordered)
            {
                SessionRow row;
                if (!rowCache.TryGetValue(s.Id, out row))
                {
                    row = new SessionRow();
                    rowCache[s.Id] = row;
                }
                row.Update(s);
                rows.Children.Add(row);
            }

            if (!entering) return;
            // Staggered entrance, like the macOS list: fade + 6px rise, 50ms apart.
            int i = 0;
            foreach (FrameworkElement row in rows.Children)
            {
                var delay = TimeSpan.FromMilliseconds(80 + 50 * i++);
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                row.Opacity = 0;
                var lift = new TranslateTransform(0, 6);
                row.RenderTransform = lift;
                row.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { BeginTime = delay, EasingFunction = ease });
                lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(380)) { BeginTime = delay, EasingFunction = ease });
            }
        }

        // --- Hover, click-through ----------------------------------------------------------------

        private void TrackHover()
        {
            if (hwnd == IntPtr.Zero || PresentationSource.FromVisual(this) == null) return;

            Native.POINT p;
            Native.GetCursorPos(out p);
            Point local;
            try { local = root.PointFromScreen(new Point(p.X, p.Y)); }
            catch (InvalidOperationException) { return; }

            double cx = CanvasW / 2;
            Rect zone;
            if (expanded)
                zone = new Rect(cx - w.Value / 2 - 10, -4, w.Value + 20, h.Value + 16);
            else
            {
                double zw = Math.Max(w.Target, 180), zh = Math.Max(h.Target, 10) + 4;
                zone = new Rect(cx - zw / 2, -4, zw, zh + 4);
            }

            // Ignore the top edge while a button is held: that's a window being dragged to snap, not a hover.
            bool dragging = (Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8000) != 0 && !expanded;
            bool inside = mode != Mode.Hidden && zone.Contains(local) && !dragging;
            DateTime now = DateTime.Now;

            if (inside)
            {
                hoverLeave = DateTime.MaxValue;
                if (hoverEnter == DateTime.MaxValue) hoverEnter = now;
                if (!expanded && (now - hoverEnter).TotalMilliseconds >= 140) SetExpanded(true);
            }
            else
            {
                hoverEnter = DateTime.MaxValue;
                if (hoverLeave == DateTime.MaxValue) hoverLeave = now;
                if (expanded && (now - hoverLeave).TotalMilliseconds >= 220) SetExpanded(false);
            }
        }

        private void SetExpanded(bool value)
        {
            if (expanded == value) return;
            expanded = value;
            SetClickThrough(!value);
            Refresh();
        }

        /// <summary>Opens the panel from the tray icon; it closes again once the pointer has been away from it.</summary>
        public void Peek()
        {
            hoverLeave = DateTime.Now.AddSeconds(2.5);
            SetExpanded(true);
        }

        // --- Housekeeping: monitor, full-screen apps, z-order, elapsed times ---------------------

        private void Housekeep()
        {
            housekeepingTicks++;
            if (settings.Display == "active" || housekeepingTicks % 4 == 0) Place();

            bool fs = settings.HideInFullscreen && FullscreenAppOnOurMonitor();
            if (fs != fullscreenHidden)
            {
                fullscreenHidden = fs;
                Refresh();
            }

            // Other topmost windows (and Explorer restarts) can bury us: re-assert without stealing focus.
            if (housekeepingTicks % 4 == 0)
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

            if (expanded && housekeepingTicks % 2 == 0)
                foreach (Session s in store.Sessions)
                {
                    SessionRow row;
                    if (rowCache.TryGetValue(s.Id, out row)) row.Update(s);
                }
        }

        private bool FullscreenAppOnOurMonitor()
        {
            int state;
            if (Native.SHQueryUserNotificationState(out state) == 0 && (state == 3 || state == 4)) return true;

            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == hwnd) return false;
            string cls = Native.GetClassName(fg);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;
            Native.RECT r;
            if (!Native.GetWindowRect(fg, out r)) return false;
            System.Drawing.Rectangle b = CurrentBounds();
            return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
        }

        // --- Animation loop ----------------------------------------------------------------------

        private void StartAnimation()
        {
            if (animating) return;
            animating = true;
            lastFrame = clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnFrame;
        }

        private void OnFrame(object sender, EventArgs e)
        {
            double now = clock.Elapsed.TotalSeconds;
            double dt = Math.Min(1.0 / 20, now - lastFrame);
            lastFrame = now;

            bool moving = false;
            // Fixed sub-steps keep the springs stable whatever the frame rate.
            int steps = Math.Max(1, (int)Math.Ceiling(dt / (1.0 / 240)));
            foreach (Spring s in springs)
                for (int i = 0; i < steps; i++)
                    moving |= s.Step(dt / steps);

            ApplyFrame();
            if (!moving)
            {
                CompositionTarget.Rendering -= OnFrame;
                animating = false;
            }
        }

        private void ApplyFrame()
        {
            double width = Math.Max(0, w.Value), height = Math.Max(0, h.Value);
            island.Width = width;
            island.Height = Math.Max(0.01, height);

            Geometry closed = NotchGeometry(width, height, outer.Value, inner.Value, true);
            fill.Data = closed;
            outline.Data = NotchGeometry(width, height, outer.Value, inner.Value, false);
            island.Clip = closed;

            double o = Math.Max(0, outer.Value);
            bar.Margin = new Thickness(o + 6, 0, o + 6, 0);
            bar.Opacity = Clamp01(barAlpha.Value);
            bar.Visibility = bar.Opacity < 0.01 ? Visibility.Hidden : Visibility.Visible;
            listHost.Opacity = Clamp01(listAlpha.Value);
            listHost.Visibility = listHost.Opacity < 0.01 ? Visibility.Hidden : Visibility.Visible;
            outline.Opacity = Clamp01(height / 6);
        }

        private static double Clamp01(double v) { return Math.Max(0, Math.Min(1, v)); }

        /// <summary>
        /// MacBook notch silhouette (same construction as NotchAgent's NotchShape): concave "ears" of radius
        /// <paramref name="o"/> where the notch leaves the top edge, convex corners of radius <paramref name="i"/> at the bottom.
        /// The open variant omits the top edge, for the hairline outline.
        /// </summary>
        private static Geometry NotchGeometry(double W, double H, double o, double i, bool closed)
        {
            o = Math.Max(0, Math.Min(o, Math.Min(W / 2, H)));
            i = Math.Max(0, Math.Min(i, Math.Min((W - 2 * o) / 2, H - o)));
            var g = new StreamGeometry();
            using (StreamGeometryContext c = g.Open())
            {
                c.BeginFigure(new Point(0, 0), closed, closed);
                c.QuadraticBezierTo(new Point(o, 0), new Point(o, o), true, true);
                c.LineTo(new Point(o, H - i), true, true);
                c.QuadraticBezierTo(new Point(o, H), new Point(o + i, H), true, true);
                c.LineTo(new Point(W - o - i, H), true, true);
                c.QuadraticBezierTo(new Point(W - o, H), new Point(W - o, H - i), true, true);
                c.LineTo(new Point(W - o, o), true, true);
                c.QuadraticBezierTo(new Point(W - o, 0), new Point(W, 0), true, true);
            }
            g.Freeze();
            return g;
        }
    }

    /// <summary>Damped spring; response/damping match SwiftUI's .spring(response:dampingFraction:).</summary>
    internal sealed class Spring
    {
        private readonly double stiffness, damping;
        public double Value, Velocity, Target;

        public Spring(double value, double response, double dampingFraction)
        {
            Value = Target = value;
            stiffness = Math.Pow(2 * Math.PI / response, 2);
            damping = 2 * dampingFraction * Math.Sqrt(stiffness);
        }

        public bool Step(double dt)
        {
            Velocity += (stiffness * (Target - Value) - damping * Velocity) * dt;
            Value += Velocity * dt;
            if (Math.Abs(Target - Value) < 0.005 && Math.Abs(Velocity) < 0.01)
            {
                Value = Target;
                Velocity = 0;
                return false;
            }
            return true;
        }
    }

    /// <summary>One session in the expanded panel: indicator, "Claude Code · project", activity, elapsed time.</summary>
    internal sealed class SessionRow : Border
    {
        private static readonly Brush Subtle = Brush(0x99, 0x99, 0x99);
        private static readonly Brush Faint = Brush(0x70, 0x70, 0x70);

        private readonly GridIndicator indicator = new GridIndicator();
        private readonly TextBlock subtitle = new TextBlock { FontSize = 11, Foreground = Subtle };
        private readonly TextBlock activity = new TextBlock { FontSize = 14, FontWeight = FontWeights.Medium, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock elapsed = new TextBlock { FontSize = 11, Foreground = Faint, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly TextBlock open = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 11,
            Foreground = Subtle,
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 1, 0),
        };
        private readonly Border hover = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = Brush(0x1F, 0x1F, 0x1F),
            BorderBrush = Brush(0x30, 0x30, 0x30),
            BorderThickness = new Thickness(1),
            Opacity = 0,
        };
        private Session session;

        public SessionRow()
        {
            Height = 58;
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;

            var grid = new Grid { Margin = new Thickness(8, 0, 12, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(indicator, 0);
            grid.Children.Add(indicator);

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
            activity.Margin = new Thickness(0, 4, 0, 0);
            texts.Children.Add(subtitle);
            texts.Children.Add(activity);
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);

            var meta = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            meta.Children.Add(elapsed);
            meta.Children.Add(open);
            Grid.SetColumn(meta, 2);
            grid.Children.Add(meta);

            var layers = new Grid();
            layers.Children.Add(hover);
            layers.Children.Add(grid);
            Child = layers;

            MouseEnter += (s, e) => Highlight(true);
            MouseLeave += (s, e) => Highlight(false);
            MouseLeftButtonUp += (s, e) =>
            {
                if (session != null && session.HostWindow != IntPtr.Zero) Native.FocusWindow(session.HostWindow);
            };
        }

        public static FrameworkElement Placeholder()
        {
            var row = new SessionRow();
            row.Cursor = Cursors.Arrow;
            row.indicator.Pattern = IndicatorPattern.Idle;
            row.subtitle.Text = "Claude Code";
            row.activity.Text = "Aucune session suivie";
            row.activity.Foreground = Subtle;
            row.elapsed.Text = "";
            return row;
        }

        public void Update(Session s)
        {
            session = s;
            indicator.Pattern = Patterns.Of(s.Effective);
            subtitle.Text = "Claude Code · " + s.Project;
            activity.Text = RowActivity(s);
            elapsed.Text = Since(s.StateSince);
            ToolTip = s.HostWindow != IntPtr.Zero ? "Afficher le terminal" : null;
        }

        /// <summary>The row has room for the full story: what to approve, or what is running and on what.</summary>
        private static string RowActivity(Session s)
        {
            string label = ToolDetail.Label(s.Tool);
            string what = label != null && s.Detail != null ? label + " · " + s.Detail : label ?? s.Detail;
            switch (s.Effective)
            {
                case AgentState.Awaiting:
                    return what != null ? "À valider : " + what : "Attend votre réponse";
                case AgentState.Working:
                    if (s.State == AgentState.Idle) return s.Subagents.Count > 1 ? s.Subagents.Count + " agents en arrière-plan" : "Agent en arrière-plan";
                    return what ?? "Réflexion…";
                default:
                    return "En attente de votre message";
            }
        }

        private void Highlight(bool on)
        {
            if (session == null) return;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            hover.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            open.BeginAnimation(OpacityProperty, new DoubleAnimation(on && session.HostWindow != IntPtr.Zero ? 1 : 0, TimeSpan.FromMilliseconds(180)));
        }

        private static string Since(DateTime t)
        {
            TimeSpan d = DateTime.Now - t;
            if (d.TotalSeconds < 45) return "à l'instant";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + " min";
            return (int)d.TotalHours + " h " + d.Minutes.ToString("00");
        }

        private static Brush Brush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
