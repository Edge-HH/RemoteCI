using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Controls;
using NAudio.Wave;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Views;

/// <summary>独立于课表窗口的非激活浮窗；关闭时立即释放音频和计时器，不把语音写入磁盘。</summary>
internal sealed class VoiceMessageWindow : Window
{
    private const string PlayGlyph = "\uEDB8";
    private const string PauseGlyph = "\uEC90";
    private const int LeftMouseButton = 0x01;

    private readonly RawSourceWaveStream _source;
    private readonly WasapiOut _output;
    private readonly FluentIcon _pauseIcon = new(PauseGlyph, 30);
    private readonly Button _pause;
    private readonly TextBlock _progress = new() { FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _progressBar = new() { Minimum = 0, Height = 32 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Stopwatch _entrance = new();
    private PixelPoint _target;
    private bool _animating = true;
    private bool _disposed;
    private bool _updatingProgress;
    private bool _seeking;
    private bool _wasLeftButtonDown;
    private string? _playbackError;

    public VoiceMessageWindow(byte[] audio, string title)
    {
        Title = title;
        Width = 240;
        Height = 116;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        // Avalonia 12 把装饰枚举改名为 WindowDecorations，改用兼容层设置无装饰窗口。
        HostApiCompat.RemoveWindowDecorations(this);
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Transparent];
        HostApiCompat.BindDynamicResource(this, FontFamilyProperty, "ContentControlThemeFontFamily");
        HostApiCompat.BindDynamicResource(this, ForegroundProperty, "TextFillColorPrimaryBrush");
        // Fluent Slider 默认在 20 高的滑块上下各预留 15，紧凑高度下会裁掉滑块底部。
        // 仅缩小本浮窗内的模板留白，继续使用宿主原生 Slider 控件及交互样式。
        _progressBar.Resources["SliderPreContentMargin"] = new GridLength(5);
        _progressBar.Resources["SliderPostContentMargin"] = new GridLength(5);
        _source = new RawSourceWaveStream(
            new MemoryStream(audio, writable: false),
            new WaveFormat(VoiceMessageRequest.SampleRate, 16, 1));
        var heading = new TextBlock
        {
            Text = title.EndsWith("语音消息", StringComparison.Ordinal) ? title[..^2] : title,
            FontSize = 16, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTip.SetTip(heading, title);
        heading.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _animating = false;
            BeginMoveDrag(e);
        };
        _pause = CreateIconButton(_pauseIcon, "暂停");
        var rewind = CreateIconButton(new FluentIcon("\uEE91", 30), "后退 5 秒");
        var close = CreateIconButton(new FluentIcon("\uE671", 30), "关闭");
        HostApiCompat.BindDynamicResource(close, ForegroundProperty, "SystemFillColorCriticalBrush");
        _pause.Click += (_, _) => TryPlaybackAction(TogglePlayback);
        rewind.Click += (_, _) =>
        {
            TryPlaybackAction(Rewind);
        };
        close.Click += (_, _) => Close();
        // 原生 Slider 拖动期间由用户控制位置，避免播放计时器把滑块拉回。
        _progressBar.AddHandler(PointerPressedEvent, (_, _) => _seeking = true,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _progressBar.AddHandler(PointerReleasedEvent, (_, _) => _seeking = false,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _progressBar.PointerCaptureLost += (_, _) => _seeking = false;
        _progressBar.PropertyChanged += (_, e) =>
        {
            if (_updatingProgress || e.Property != Slider.ValueProperty || _disposed) return;
            TryPlaybackAction(() => _source.CurrentTime = TimeSpan.FromSeconds(_progressBar.Value));
        };
        var surface = new Border
        {
            CornerRadius = new CornerRadius(10),
            Child = new Grid
            {
                RowSpacing = 2,
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                Children =
                {
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                        ColumnSpacing = 10,
                        Children =
                        {
                            heading,
                            new Border { Child = _progress, [Grid.ColumnProperty] = 1 },
                        },
                    },
                    new Border { Child = _progressBar, [Grid.RowProperty] = 1, VerticalAlignment = VerticalAlignment.Center },
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,*,*"),
                        [Grid.RowProperty] = 2,
                        Children = { _pause, rewind, close },
                    },
                },
            },
        };
        surface.Classes.Add("popup-bg");
        Grid.SetColumn(rewind, 1);
        Grid.SetColumn(close, 2);
        Content = surface;
        _output = new WasapiOut();
        try { _output.Init(_source); }
        catch { _output.Dispose(); _source.Dispose(); throw; }
        _progressBar.Maximum = _source.TotalTime.TotalSeconds;
        _output.PlaybackStopped += (_, e) => Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            SetPauseButton(playing: false);
            if (e.Exception is not null) _playbackError = "播放失败，请检查音频设备后关闭重试";
        });
        Opened += (_, _) =>
        {
            var area = (Screens.ScreenFromWindow(this) ?? Screens.Primary)?.WorkingArea;
            if (area is { } bounds)
            {
                var scale = RenderScaling;
                _target = new PixelPoint(bounds.X + (bounds.Width - (int)(Width * scale)) / 2,
                    Math.Max(bounds.Y, bounds.Bottom - (int)((Height + 48) * scale)));
                Position = new PixelPoint(_target.X, _target.Y + (int)(32 * scale));
            }
            else { _target = Position; }
            _wasLeftButtonDown = IsLeftButtonDown();
            _entrance.Start();
            _timer.Start();
        };
        _timer.Tick += (_, _) =>
        {
            if (_animating)
            {
                var fraction = Math.Min(1, _entrance.Elapsed.TotalMilliseconds / 280);
                Position = new PixelPoint(_target.X, _target.Y + (int)(32 * RenderScaling * Math.Pow(1 - fraction, 3)));
                if (fraction >= 1) _animating = false;
            }
            if (CloseOnOutsidePointerPressed()) return;
            UpdateProgress();
        };
        Closed += (_, _) => DisposeAudio();
    }

    public void StartPlayback() { _output.Play(); UpdateProgress(); }

    private void TogglePlayback()
    {
        if (_output.PlaybackState == PlaybackState.Playing)
        {
            _output.Pause();
            SetPauseButton(playing: false);
        }
        else
        {
            if (_source.Position >= _source.Length) _source.Position = 0;
            _output.Play();
            SetPauseButton(playing: true);
        }
    }

    private void UpdateProgress()
    {
        _progress.Text = _playbackError is null ? $"{Math.Ceiling(_source.TotalTime.TotalSeconds):0}″" : "播放失败";
        ToolTip.SetTip(_progress, _playbackError ?? $"{_source.CurrentTime:mm\\:ss} / {_source.TotalTime:mm\\:ss}");
        _updatingProgress = true;
        if (!_seeking)
            _progressBar.Value = Math.Min(_source.CurrentTime.TotalSeconds, _progressBar.Maximum);
        _updatingProgress = false;
        if (_output.PlaybackState == PlaybackState.Stopped) SetPauseButton(playing: false);
    }

    private void Rewind()
    {
        _source.CurrentTime = TimeSpan.FromSeconds(Math.Max(0, _source.CurrentTime.TotalSeconds - 5));
        UpdateProgress();
    }

    private void TryPlaybackAction(Action action)
    {
        // 播放过程中拔掉音频设备时，让用户关闭重试，不把设备异常抛到宿主 UI 线程。
        try { action(); }
        catch (Exception)
        {
            _playbackError = "播放失败，请检查音频设备后关闭重试";
            _pause.IsEnabled = false;
            UpdateProgress();
        }
    }

    private void SetPauseButton(bool playing)
    {
        // Glyph 由 FluentAvalonia 的图标基类声明，该基类在新宿主中改名为 FAFontIcon，
        // 直接绑定会抛 MissingMethodException，这里按属性名写入图标字形。
        HostApiCompat.WriteProperty(_pauseIcon, "Glyph", playing ? PauseGlyph : PlayGlyph);
        ToolTip.SetTip(_pause, playing ? "暂停" : "播放");
    }

    internal static Button CreateIconButton(FluentIcon icon, string tooltip)
    {
        var button = new Button
        {
            Content = icon,
            Width = 40,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        HostApiCompat.BindDynamicResource(button, ThemeProperty, "TransparentButton");
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    private bool CloseOnOutsidePointerPressed()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var isDown = IsLeftButtonDown();
        var isNewPress = isDown && !_wasLeftButtonDown;
        _wasLeftButtonDown = isDown;
        if (!isNewPress || !GetCursorPos(out var point)) return false;

        var width = (int)Math.Ceiling(Bounds.Width * RenderScaling);
        var height = (int)Math.Ceiling(Bounds.Height * RenderScaling);
        var isOutside = point.X < Position.X || point.X >= Position.X + width ||
                        point.Y < Position.Y || point.Y >= Position.Y + height;
        if (!isOutside) return false;

        Close();
        return true;
    }

    private static bool IsLeftButtonDown() =>
        OperatingSystem.IsWindows() && (GetAsyncKeyState(LeftMouseButton) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    public void DisposeAudio()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _output.Dispose();
        _source.Dispose();
    }
}
