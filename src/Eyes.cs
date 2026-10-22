using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Minou
{
    internal enum EyeMood
    {
        /// <summary>Claude is working: plain eyes, natural blinking.</summary>
        Normal,
        /// <summary>An approval is waiting: wide eyes that blink less, like someone paying attention.</summary>
        Alert,
        /// <summary>Task done: smiling "^ ^".</summary>
        Happy,
        /// <summary>Turn failed: squeezed "&gt; &lt;".</summary>
        Error,
        /// <summary>Nothing going on: heavy, half-closed lids and slow blinks.</summary>
        Sleepy,
    }

    /// <summary>
    /// Minou's two eyes. They blink together like a person's: the upper lid drops fast, rests shut for a moment and lifts
    /// a little slower, at irregular intervals with the occasional double blink. Their shape follows the session state.
    /// </summary>
    internal sealed class Eyes : StackPanel
    {
        private const double CellW = 6, CellH = 9, EyeW = 4.5, EyeH = 7, Gap = 3.5;
        private const double Stroke = 1.5;

        private static readonly Random Rng = new Random();
        private static readonly Brush White = Frozen(Color.FromRgb(0xF4, 0xF1, 0xEC));

        // Shared by both eyes so they always move together.
        private readonly ScaleTransform lid = new ScaleTransform(1, 1);
        private readonly ScaleTransform mood = new ScaleTransform(1, 1);
        private readonly UIElement[] round = new UIElement[2], smile = new UIElement[2], squeeze = new UIElement[2];
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private EyeMood current = EyeMood.Normal;

        public Eyes()
        {
            Orientation = Orientation.Horizontal;
            VerticalAlignment = VerticalAlignment.Center;
            IsHitTestVisible = false;

            var shape = new TransformGroup();
            shape.Children.Add(mood);
            shape.Children.Add(lid);

            for (int i = 0; i < 2; i++)
            {
                bool left = i == 0;
                var cell = new Grid { Width = CellW, Height = CellH, Margin = new Thickness(left ? 0 : Gap, 0, 0, 0) };

                round[i] = new Rectangle
                {
                    Width = EyeW,
                    Height = EyeH,
                    RadiusX = EyeW / 2,
                    RadiusY = EyeW / 2,
                    Fill = White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    // Shrinking toward a point low in the eye reads as the upper lid coming down.
                    RenderTransformOrigin = new Point(0.5, 0.72),
                    RenderTransform = shape,
                };
                // "^": an arch, like eyes closed by a smile.
                smile[i] = Line("M 0.75,6 Q 3,1.6 5.25,6");
                // ">" for the left eye, "<" for the right one.
                squeeze[i] = Line(left ? "M 1.2,2 L 4.8,4.5 L 1.2,7" : "M 4.8,2 L 1.2,4.5 L 4.8,7");

                cell.Children.Add(round[i]);
                cell.Children.Add(smile[i]);
                cell.Children.Add(squeeze[i]);
                Children.Add(cell);
            }

            timer.Tick += (s, e) => Blink();
            Loaded += (s, e) => ScheduleNext();
            Unloaded += (s, e) => timer.Stop();
            Apply(EyeMood.Normal, false);
        }

        public static double TotalWidth { get { return CellW * 2 + Gap; } }

        public EyeMood Mood
        {
            get { return current; }
            set
            {
                if (value == current) return;
                current = value;
                Apply(value, true);
                ScheduleNext();
            }
        }

        private void Apply(EyeMood m, bool animate)
        {
            bool showRound = m == EyeMood.Normal || m == EyeMood.Alert || m == EyeMood.Sleepy;
            for (int i = 0; i < 2; i++)
            {
                Fade(round[i], showRound ? 1 : 0, animate);
                Fade(smile[i], m == EyeMood.Happy ? 1 : 0, animate);
                Fade(squeeze[i], m == EyeMood.Error ? 1 : 0, animate);
            }

            double sx = m == EyeMood.Alert ? 1.3 : 1, sy = m == EyeMood.Alert ? 1.3 : m == EyeMood.Sleepy ? 0.42 : 1;
            // Surprise pops with a small overshoot; everything else settles softly.
            IEasingFunction ease = m == EyeMood.Alert
                ? (IEasingFunction)new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 }
                : new CubicEase { EasingMode = EasingMode.EaseOut };
            var d = TimeSpan.FromMilliseconds(animate ? 260 : 0);
            mood.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(sx, d) { EasingFunction = ease });
            mood.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(sy, d) { EasingFunction = ease });
            if (!showRound) lid.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }

        private static void Fade(UIElement e, double to, bool animate)
        {
            e.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(animate ? 160 : 0)));
        }

        // --- Blinking ----------------------------------------------------------------------------

        private void ScheduleNext()
        {
            timer.Stop();
            if (current == EyeMood.Happy || current == EyeMood.Error) return; // closed-shape moods don't blink
            double r = Math.Pow(Rng.NextDouble(), 1.6);
            // Human pace (~2–7 s); attentive eyes hold longer, sleepy ones drift shut rarely.
            double seconds = current == EyeMood.Alert ? 3.5 + r * 6.0
                : current == EyeMood.Sleepy ? 4.0 + r * 7.0
                : 2.0 + r * 5.0;
            timer.Interval = TimeSpan.FromSeconds(seconds);
            timer.Start();
        }

        private void Blink()
        {
            timer.Stop();
            bool sleepy = current == EyeMood.Sleepy;
            bool twice = !sleepy && Rng.NextDouble() < 0.12;
            var close = new SineEase { EasingMode = EasingMode.EaseIn };
            var open = new CubicEase { EasingMode = EasingMode.EaseOut };

            var frames = new DoubleAnimationUsingKeyFrames();
            double t = 0;
            for (int i = 0; i < (twice ? 2 : 1); i++)
            {
                // A sleepy blink is a slow droop and a long rest before the lids lift again.
                double down = sleepy ? 220 + Rng.Next(80) : 65 + Rng.Next(25);
                double shut = sleepy ? 260 + Rng.Next(240) : 35 + Rng.Next(30);
                double up = sleepy ? 380 + Rng.Next(120) : 120 + Rng.Next(50);
                frames.KeyFrames.Add(new EasingDoubleKeyFrame(0.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t += down)), close));
                frames.KeyFrames.Add(new EasingDoubleKeyFrame(0.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t += shut))));
                frames.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t += up)), open));
                t += 90; // pause before the second blink of a double
            }
            frames.Completed += (s, e) => ScheduleNext();
            lid.BeginAnimation(ScaleTransform.ScaleYProperty, frames);
        }

        // --- Helpers -----------------------------------------------------------------------------

        private static Path Line(string data)
        {
            return new Path
            {
                Data = Geometry.Parse(data),
                Stroke = White,
                StrokeThickness = Stroke,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = CellW,
                Height = CellH,
                Opacity = 0,
            };
        }

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
