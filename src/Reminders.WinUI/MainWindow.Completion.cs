using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Reminders.Windows.Services;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.UI;

namespace Reminders.Windows;

public sealed partial class MainWindow
{
    private static readonly Color[] RibbonColors =
    [
        Color.FromArgb(255, 50, 127, 223),
        Color.FromArgb(255, 124, 82, 214),
        Color.FromArgb(255, 20, 165, 142),
        Color.FromArgb(255, 236, 96, 132),
        Color.FromArgb(255, 245, 177, 53)
    ];

    private static void PlayCompletionSound()
    {
        const uint asyncFileWithoutFallback = 0x00020003;
        try { PlaySound(Path.Combine(AppContext.BaseDirectory, "Assets", "completion.wav"), IntPtr.Zero, asyncFileWithoutFallback); }
        catch (Exception error) { AppLog.Error("Completion sound could not be played", error); }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string sound, IntPtr module, uint flags);

    private async Task ShowCompletionRibbonsAsync(FrameworkElement anchor)
    {
        if (_windowClosed || Root.XamlRoot is null || Root.ActualWidth <= 0 || Root.ActualHeight <= 0) return;
        var overlay = new Canvas { IsHitTestVisible = false, Tag = "CompletionRibbons" };
        Grid.SetRowSpan(overlay, 2);
        try
        {
            var point = anchor.TransformToVisual(Root).TransformPoint(
                new Point(Math.Min(90, anchor.ActualWidth / 4), Math.Min(24, anchor.ActualHeight / 2)));
            var origin = new Vector3(
                (float)Math.Clamp(point.X, 0, Root.ActualWidth),
                (float)Math.Clamp(point.Y, 0, Math.Max(0, Root.ActualHeight - 95)),
                0);
            Root.Children.Add(overlay);
            var compositor = ElementCompositionPreview.GetElementVisual(overlay).Compositor;
            var ribbons = compositor.CreateContainerVisual();
            ribbons.Size = new Vector2((float)Root.ActualWidth, (float)Root.ActualHeight);
            ribbons.Clip = compositor.CreateInsetClip();
            ElementCompositionPreview.SetElementChildVisual(overlay, ribbons);

            var random = Random.Shared;
            var linear = compositor.CreateLinearEasingFunction();
            for (var index = 0; index < 20; index++)
            {
                var width = random.Next(3, 6);
                var length = random.Next(9, 18);
                var ribbon = compositor.CreateSpriteVisual();
                ribbon.Size = new Vector2(width, length);
                ribbon.CenterPoint = new Vector3(width / 2f, length / 2f, 0);
                ribbon.RotationAxis = Vector3.Normalize(new Vector3(random.NextSingle(), 0.6f, 0.8f));
                ribbon.Brush = compositor.CreateColorBrush(RibbonColors[index % RibbonColors.Length]);
                ribbons.Children.InsertAtTop(ribbon);

                var duration = TimeSpan.FromMilliseconds(random.Next(900, 1320));
                var horizontalSpeed = random.Next(-170, 171);
                var upwardSpeed = random.Next(48, 104);
                var gravity = random.Next(300, 420);
                var sway = random.Next(6, 17);
                var position = compositor.CreateVector3KeyFrameAnimation();
                position.Duration = duration;
                for (var frame = 0; frame <= 12; frame++)
                {
                    var progress = frame / 12f;
                    var seconds = (float)duration.TotalSeconds * progress;
                    var drift = horizontalSpeed * seconds / (1 + 1.2f * seconds) + MathF.Sin(9 * seconds) * sway;
                    var fall = -upwardSpeed * seconds + gravity * seconds * seconds / 2;
                    position.InsertKeyFrame(progress, origin + new Vector3(drift, fall, 0), linear);
                }

                var spin = compositor.CreateScalarKeyFrameAnimation();
                spin.Duration = duration;
                var angle = random.Next(-180, 181);
                spin.InsertKeyFrame(0, angle);
                spin.InsertKeyFrame(1, angle + random.Next(260, 640) * (index % 2 == 0 ? 1 : -1), linear);
                var fade = compositor.CreateScalarKeyFrameAnimation();
                fade.Duration = duration;
                fade.InsertKeyFrame(0, 1);
                fade.InsertKeyFrame(0.62f, 1);
                fade.InsertKeyFrame(1, 0, linear);
                ribbon.StartAnimation("Offset", position);
                ribbon.StartAnimation("RotationAngleInDegrees", spin);
                ribbon.StartAnimation("Opacity", fade);
            }

            await Task.Delay(1400);
        }
        catch (Exception error) { AppLog.Error("Completion ribbons could not be displayed", error); }
        finally
        {
            if (!_windowClosed) Root.Children.Remove(overlay);
        }
    }
}
