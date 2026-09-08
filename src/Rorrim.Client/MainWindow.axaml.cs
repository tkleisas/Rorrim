using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Rorrim.Client.Streaming;
using Rorrim.Shared.Contracts;

namespace Rorrim.Client;

public sealed partial class MainWindow : Window
{
    private BrokerSessionClient? _session;
    private CancellationTokenSource? _cts;
    private WriteableBitmap? _bitmap;
    private double _lastXNorm, _lastYNorm;
    private bool _connected;

    public MainWindow()
    {
        InitializeComponent();
        Viewer.PointerMoved += OnViewerPointerMoved;
        Viewer.PointerPressed += OnViewerPointerButton;
        Viewer.PointerReleased += OnViewerPointerButton;
        Viewer.PointerWheelChanged += OnViewerPointerWheel;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;

        // Auto-connect if a broker address is passed as the first command-line argument.
        var cmdArgs = Environment.GetCommandLineArgs();
        if (cmdArgs.Length > 1 && !string.IsNullOrWhiteSpace(cmdArgs[1]))
        {
            BrokerBox.Text = cmdArgs[1];
            Dispatcher.UIThread.Post(() => OnConnectClick(null, null!));
        }
    }

    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (_connected) return;
        string broker = BrokerBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(broker))
        {
            StatusText.Text = "Enter a broker address.";
            return;
        }

        ConnectButton.IsEnabled = false;
        DisconnectButton.IsEnabled = true;
        StatusText.Text = "Connecting...";
        _cts = new CancellationTokenSource();

        string displayId = (DisplayBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "0";

        try
        {
            _session = new BrokerSessionClient(
                broker,
                OnFrameAsync,
                OnStatus,
                OnDisconnected);

            _ = Task.Run(() => _session.StartAsync(displayId, _cts.Token), _cts.Token);
            _connected = true;
            StatusText.Text = "Connected.";
            IdleText.IsVisible = false;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Connect failed: " + ex.Message;
            OnDisconnected(CancellationToken.None);
        }
    }

    private void OnDisconnectClick(object? sender, RoutedEventArgs e) =>
        _ = TeardownAsync();

    private async Task TeardownAsync()
    {
        _cts?.Cancel();
        await Task.CompletedTask;
        _session?.Dispose();
        _session = null;
        ConnectButton.IsEnabled = true;
        DisconnectButton.IsEnabled = false;
        IdleText.IsVisible = true;
        StatusText.Text = "";
        _connected = false;
    }

    private void OnDisconnected(CancellationToken ct)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            await TeardownAsync();
        });
    }

    private void OnStatus(SessionStatus.Types.StatusKind kind, string message) =>
        Dispatcher.UIThread.Post(() => StatusText.Text = $"{kind}: {message}");

    private void OnFrame(DecodedFrame frame)
    {
        Dispatcher.UIThread.Post(() => Render(frame));
    }

    private ValueTask OnFrameAsync(DecodedFrame frame)
    {
        OnFrame(frame);
        return ValueTask.CompletedTask;
    }

    private void Render(DecodedFrame frame)
    {
        if (StatusText.Text != "Streaming.")
            StatusText.Text = "Streaming.";
        if (_bitmap is null || _bitmap.PixelSize.Width != frame.Width || _bitmap.PixelSize.Height != frame.Height)
            _bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        using (var fb = _bitmap.Lock())
        {
            unsafe
            {
                // Frames decode as BGRA, matching WriteableBitmap's Bgra8888.
                byte* dst = (byte*)fb.Address.ToPointer();
                byte[] bgra = frame.Bgra;
                int stride = frame.Width * 4;
                for (int i = 0; i < frame.Width * frame.Height; i++)
                {
                    int o = i * 4;
                    dst[o] = bgra[o];
                    dst[o + 1] = bgra[o + 1];
                    dst[o + 2] = bgra[o + 2];
                    dst[o + 3] = 255;
                }
            }
        }
        Viewer.Source = _bitmap;
    }

    // --- input forwarding ---

    private void OnViewerPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_connected || _session is null) return;
        var pos = e.GetPosition(Viewer);
        double xn = pos.X / Viewer.Bounds.Width;
        double yn = pos.Y / Viewer.Bounds.Height;
        _lastXNorm = xn; _lastYNorm = yn;
        _ = _session.SendAsync(new ClientToServer { Input = InputEncoder.Move(xn, yn) }, _cts!.Token);
    }

    private void OnViewerPointerButton(object? sender, PointerEventArgs e)
    {
        if (!_connected || _session is null) return;
        var props = e.GetCurrentPoint(Viewer).Properties;
        var btn = MapButton(props);
        if (btn is null) return;
        bool down = e.RoutedEvent == PointerPressedEvent;
        var input = down ? InputEncoder.ButtonDown(btn.Value, _lastXNorm, _lastYNorm)
                         : InputEncoder.ButtonUp(btn.Value, _lastXNorm, _lastYNorm);
        _ = _session.SendAsync(new ClientToServer { Input = input }, _cts!.Token);
    }

    private void OnViewerPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!_connected || _session is null) return;
        double delta = e.Delta.Y > 0 ? 1 : -1;
        _ = _session.SendAsync(new ClientToServer { Input = InputEncoder.Scroll(delta) }, _cts!.Token);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e) => SendKey(e.Key, down: true);
    private void OnKeyUp(object? sender, KeyEventArgs e) => SendKey(e.Key, down: false);

    private void SendKey(Key key, bool down)
    {
        if (!_connected || _session is null) return;
        var (vk, sc, ext) = MapKey(key);
        if (vk == 0) return;
        _ = _session.SendAsync(new ClientToServer { Input = InputEncoder.Key(vk, sc, down, ext) }, _cts!.Token);
    }

    private static uint? MapButton(PointerPointProperties props)
    {
        if (props.IsLeftButtonPressed) return 0;
        if (props.IsRightButtonPressed) return 1;
        if (props.IsMiddleButtonPressed) return 2;
        return null;
    }

    private static (uint vk, uint scan, bool ext) MapKey(Key key)
    {
        return key switch
        {
            Key.LeftShift => (0xA0, 0, false),
            Key.RightShift => (0xA1, 0, true),
            Key.LeftCtrl => (0xA2, 0, false),
            Key.RightCtrl => (0xA3, 0, true),
            Key.LeftAlt => (0xA4, 0, false),
            Key.RightAlt => (0xA5, 0, true),
            Key.Enter => (0x0D, 0, false),
            Key.Tab => (0x09, 0, false),
            Key.Back => (0x08, 0, false),
            Key.Space => (0x20, 0, false),
            Key.Escape => (0x1B, 0, false),
            _ => (0, 0, false)
        };
    }
}
