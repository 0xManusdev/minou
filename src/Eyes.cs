using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Minou
{
    /// <summary>
    /// Minou's two eyes. They blink together like a person's: the upper lid drops fast, rests shut for a moment and lifts
    /// a little slower, at irregular intervals (mostly every 2–4 s, sometimes longer) with the occasional double blink.
    /// </summary>
    internal sealed class Eyes : StackPanel
    {
        private const double EyeW = 4.5, EyeH = 7, Gap = 4.5;

        private static readonly Random Rng = new Random();
        private readonly ScaleTransform lid = new ScaleTransform(1, 1);
        private readonly DispatcherTimer timer = new DispatcherTimer();

        public Eyes()
        {
            Orientation = Orientation.Horizontal;
            VerticalAlignment = VerticalAlignment.Center;
            IsHitTestVisible = false;

            for (int i = 0; i < 2; i++)
            {
                var eye = new Rectangle
                {
                    Width = EyeW,
                    Height = EyeH,
                    RadiusX = EyeW / 2,
                    RadiusY = EyeW / 2,
                    Fill = new SolidColorBrush(Color.FromRgb(0xF4, 0xF1, 0xEC)),
                    // Shrinking toward a point low in the eye reads as the upper lid coming down.
                    RenderTransformOrigin = new Point(0.5, 0.72),
                    RenderTransform = lid,
                    Margin = new Thickness(i == 0 ? 0 : Gap, 0, 0, 0),
                };
                Children.Add(eye);
            }

            timer.Tick += (s, e) => Blink();
            Loaded += (s, e) => ScheduleNext();
            Unloaded += (s, e) => timer.Stop();
        }

        public static double TotalWidth { get { return EyeW * 2 + Gap; } }

        private void ScheduleNext()
        {
            // Skewed toward short gaps, like human blinking: ~2 s at the shortest, ~7 s at the longest.
            double seconds = 2.0 + Math.Pow(Rng.NextDouble(), 1.6) * 5.0;
            timer.Stop();
            timer.Interval = TimeSpan.FromSeconds(seconds);
            timer.Start();
        }

        private void Blink()
        {
            timer.Stop();
            bool twice = Rng.NextDouble() < 0.12;
            var close = new SineEase { EasingMode = EasingMode.EaseIn };
            var open = new CubicEase { EasingMode = EasingMode.EaseOut };

            var frames = new DoubleAnimationUsingKeyFrames();
            double t = 0;
            for (int i = 0; i < (twice ? 2 : 1); i++)
            {
                double down = 65 + Rng.Next(25), shut = 35 + Rng.Next(30), up = 120 + Rng.Next(50);
                frames.KeyFrames.Add(new EasingDoubleKeyFrame(0.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t += down)), close));
                frames.KeyFrames.Add(new EasingDoubleKeyFrame(0.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t += shut))));
                frames.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t += up)), open));
                t += 90; // pause before the second blink of a double
            }
            frames.Completed += (s, e) => ScheduleNext();
            lid.BeginAnimation(ScaleTransform.ScaleYProperty, frames);
        }
    }
}
