using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace NotchAgent
{
    internal enum IndicatorPattern { Idle, Working, Awaiting, Done, Error }

    /// <summary>
    /// The 3×3 pixel-grid animations from NotchAgent for macOS (cells are row-major: 0 = top-left, 8 = bottom-right),
    /// plus a ✓ and a ✕ for the finish and failure flashes.
    /// </summary>
    internal static class Patterns
    {
        private static readonly Random Rng = new Random();

        // snake, single horizontal, single vertical, staggering horizontal, staggering vertical
        private static readonly int[][] Paths =
        {
            new[] { 0, 1, 2, 5, 4, 3, 6, 7, 8, 5, 4, 3 },
            new[] { 3, 4, 5 },
            new[] { 1, 4, 7 },
            new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 },
            new[] { 6, 3, 0, 7, 4, 1, 8, 5, 2 },
        };
        private static readonly int[] Trails = { 4, 2, 2, 3, 3 };
        private static readonly int[] AwaitingPath = { 1, 4, 7 };
        private static readonly bool[] Check = { false, false, true, true, false, true, false, true, false };
        private static readonly bool[] Cross = { true, false, true, false, true, false, true, false, true };

        public static readonly Color Cream = Color.FromRgb(0xFF, 0xCC, 0x9B);
        public static readonly Color Orange = Color.FromRgb(0xCC, 0x8C, 0x40);
        public static readonly Color Red = Color.FromRgb(0xFF, 0x33, 0x33);
        public static readonly Color Grey = Color.FromRgb(0x59, 0x59, 0x59);
        public static readonly Color Green = Color.FromRgb(0x3D, 0xD6, 0x8C);

        public static int RandomAnimation() { return Rng.Next(Paths.Length); }

        public static Color ColorOf(IndicatorPattern p)
        {
            switch (p)
            {
                case IndicatorPattern.Working: return Cream;
                case IndicatorPattern.Awaiting: return Red;
                case IndicatorPattern.Done: return Green;
                case IndicatorPattern.Error: return Red;
                default: return Grey;
            }
        }

        /// <summary>Milliseconds between two animation steps.</summary>
        public static int StepMs(IndicatorPattern p)
        {
            switch (p)
            {
                case IndicatorPattern.Working: return 200;
                case IndicatorPattern.Awaiting: return 400;
                case IndicatorPattern.Idle: return 380;
                default: return 0;
            }
        }

        public static double Opacity(int cell, IndicatorPattern p, int head, int animation)
        {
            switch (p)
            {
                case IndicatorPattern.Awaiting:
                    for (int i = 0; i < 2; i++)
                        if (AwaitingPath[(head - i + 3 * 1000) % 3] == cell) return 0.35 + (1 - i) * 0.65;
                    return 0;
                case IndicatorPattern.Done:
                    return Check[cell] ? 1 : 0.12;
                case IndicatorPattern.Error:
                    return Cross[cell] ? 1 : 0.12;
                default:
                    int[] path = Paths[animation];
                    int trail = Trails[animation];
                    for (int i = 0; i < trail; i++)
                        if (path[(head - i + path.Length * 1000) % path.Length] == cell)
                            return 0.30 + (double)(trail - 1 - i) / (trail - 1) * 0.70;
                    return 0;
            }
        }

        public static IndicatorPattern Of(AgentState s)
        {
            switch (s)
            {
                case AgentState.Working: return IndicatorPattern.Working;
                case AgentState.Awaiting: return IndicatorPattern.Awaiting;
                default: return IndicatorPattern.Idle;
            }
        }
    }

    /// <summary>A 15×15 grid of 5px cells with eased per-cell fades and, while working, a warm glow behind it.</summary>
    internal sealed class GridIndicator : Grid
    {
        private const double Cell = 5;
        private readonly Rectangle[] cells = new Rectangle[9];
        private readonly double[] targets = new double[9];
        private readonly Ellipse glow;
        private readonly DispatcherTimer timer;
        private IndicatorPattern pattern = IndicatorPattern.Idle;
        private int head;
        private int animation = Patterns.RandomAnimation();

        public GridIndicator()
        {
            Width = 32;
            Height = 32;
            IsHitTestVisible = false;

            glow = new Ellipse
            {
                Width = 18,
                Height = 18,
                Fill = new SolidColorBrush(Color.FromArgb(0x8C, Patterns.Orange.R, Patterns.Orange.G, Patterns.Orange.B)),
                Effect = new BlurEffect { Radius = 9, RenderingBias = RenderingBias.Performance },
                Opacity = 0,
            };
            Children.Add(glow);

            var canvas = new Canvas { Width = Cell * 3, Height = Cell * 3, SnapsToDevicePixels = true };
            for (int i = 0; i < 9; i++)
            {
                var r = new Rectangle { Width = Cell, Height = Cell, Opacity = 0, Fill = new SolidColorBrush(Patterns.Grey) };
                Canvas.SetLeft(r, (i % 3) * Cell);
                Canvas.SetTop(r, (i / 3) * Cell);
                canvas.Children.Add(r);
                cells[i] = r;
                targets[i] = -1;
            }
            Children.Add(canvas);

            timer = new DispatcherTimer(DispatcherPriority.Render);
            timer.Tick += (s, e) => { head++; Render(); };
            Loaded += (s, e) => Restart();
            Unloaded += (s, e) => timer.Stop();
        }

        public IndicatorPattern Pattern
        {
            get { return pattern; }
            set
            {
                if (pattern == value) return;
                if (value == IndicatorPattern.Working && pattern != IndicatorPattern.Working) animation = Patterns.RandomAnimation();
                pattern = value;
                head = 0;
                Restart();
            }
        }

        private void Restart()
        {
            timer.Stop();
            Color c = Patterns.ColorOf(pattern);
            foreach (var r in cells) ((SolidColorBrush)r.Fill).Color = c;
            glow.BeginAnimation(OpacityProperty, new DoubleAnimation(pattern == IndicatorPattern.Working ? 1 : 0, TimeSpan.FromMilliseconds(400)));
            Render();
            int step = Patterns.StepMs(pattern);
            if (step > 0 && IsLoaded)
            {
                timer.Interval = TimeSpan.FromMilliseconds(step);
                timer.Start();
            }
        }

        private void Render()
        {
            for (int i = 0; i < 9; i++)
            {
                double t = Patterns.Opacity(i, pattern, head, animation);
                if (t == targets[i]) continue;
                targets[i] = t;
                var a = new DoubleAnimation(t, TimeSpan.FromMilliseconds(220)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                cells[i].BeginAnimation(OpacityProperty, a);
            }
        }
    }
}
