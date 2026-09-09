using System.Reflection;
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
    private bool _connected;
    private bool _suppressDisplaySelection;

    // mTLS material from the command line (--pfx <path> --pfx-password <pw> --ca <pem>).
    private readonly string? _pfxPath;
    private readonly string? _pfxPassword;
    private readonly string? _caPath;

    public MainWindow()
    {
        InitializeComponent();
        // The stamped version already carries the tag's "v" prefix (e.g. "v0.0.1-dirty").
        Title = $"Rorrim Remote {GetVersion()}";
        Viewer.PointerMoved += OnViewerPointerMoved;
        Viewer.PointerPressed += OnViewerPointerButton;
        Viewer.PointerReleased += OnViewerPointerButton;
        Viewer.PointerWheelChanged += OnViewerPointerWheel;
        DisplayBox.SelectionChanged += OnDisplaySelectionChanged;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;

        // Parse the command line: one positional broker address plus optional TLS flags
        // (--pfx <path>, --pfx-password <pw>, --ca <pem>), in "--f v" or "--f=v" form.
        string? address = null;
        var cmdArgs = Environment.GetCommandLineArgs();
        for (int i = 1; i < cmdArgs.Length; i++)
        {
            string arg = cmdArgs[i];
            string? value = null;
            string? flag = null;
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                int eq = arg.IndexOf('=');
                if (eq >= 0)
                {
                    flag = arg[..eq];
                    value = arg[(eq + 1)..];
                }
                else if (i + 1 < cmdArgs.Length && !cmdArgs[i + 1].StartsWith("--"))
                {
                    flag = arg;
                    value = cmdArgs[++i];
                }
                else
                {
                    flag = arg;
                }
            }

            switch (flag)
            {
                case "--pfx": _pfxPath = value; break;
                case "--pfx-password": _pfxPassword = value; break;
                case "--ca": _caPath = value; break;
                case null when address is null: address = arg; break;
            }
        }

        // Auto-connect if a broker address is passed on the command line.
        if (!string.IsNullOrWhiteSpace(address))
        {
            BrokerBox.Text = address;
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

        string displayId = GetSelectedDisplayId();

        try
        {
            var clientCert = _pfxPath is null ? null : MutualTls.LoadClientPfx(_pfxPath, _pfxPassword);
            var trustedCa = _caPath is null ? null : MutualTls.LoadCaPem(_caPath);

            _session = new BrokerSessionClient(
                broker,
                OnFrameAsync,
                OnStatus,
                OnDisconnected,
                clientCertificate: clientCert,
                trustedCa: trustedCa,
                onDisplays: OnDisplays);

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

    /// <summary>Version from the git tag stamped at build time (see Directory.Build.targets).</summary>
    private static string GetVersion()
    {
        string? v = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(v) ? "dev" : v;
    }

    private string GetSelectedDisplayId()
    {
        if (DisplayBox.SelectedItem is ComboBoxItem item && item.Tag is string tag && tag.Length > 0)
            return tag;
        return "0";
    }

    /// <summary>
    /// Populates the display dropdown from the agent's display list. Tag holds the display id sent
    /// on the wire; content is the human-readable label.
    /// </summary>
    private void OnDisplays(DisplayListReply displays)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _suppressDisplaySelection = true;
            try
            {
                int keepIndex = DisplayBox.SelectedIndex;
                DisplayBox.Items.Clear();
                for (int i = 0; i < displays.Displays.Count; i++)
                {
                    var d = displays.Displays[i];
                    var label = d.IsPrimary ? $"[P] {d.DisplayId} ({d.Width}x{d.Height})"
                                            : $"{d.DisplayId} ({d.Width}x{d.Height})";
                    DisplayBox.Items.Add(new ComboBoxItem { Content = label, Tag = d.DisplayId });
                }
                if (DisplayBox.Items.Count > 0)
                    DisplayBox.SelectedIndex = keepIndex >= 0 && keepIndex < DisplayBox.Items.Count ? keepIndex : 0;
            }
            finally
            {
                _suppressDisplaySelection = false;
            }
        });
    }

    private void OnDisplaySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Programmatic repopulation and pre-connect selection changes are not switch requests.
        if (_suppressDisplaySelection || !_connected || _session is null) return;
        string displayId = GetSelectedDisplayId();
        _ = _session.SendAsync(new ClientToServer { Hello = new Hello { DisplayId = displayId } }, _cts!.Token);
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
                int srcStride = frame.Width * 4;
                int dstStride = fb.RowBytes;
                for (int y = 0; y < frame.Height; y++)
                {
                    byte* dstRow = dst + y * dstStride;
                    int srcRow = y * srcStride;
                    for (int x = 0; x < frame.Width; x++)
                    {
                        int o = srcRow + x * 4;
                        int d = x * 4;
                        dstRow[d] = bgra[o];
                        dstRow[d + 1] = bgra[o + 1];
                        dstRow[d + 2] = bgra[o + 2];
                        dstRow[d + 3] = 255;
                    }
                }
            }
        }
        // Re-assigning the same bitmap instance does not dirty the Image control (Avalonia skips
        // rendering when Source is reference-identical), so force a re-render to pick up the
        // mutated pixels.
        Viewer.Source = _bitmap;
        Viewer.InvalidateVisual();
    }

    // --- input forwarding ---

    /// <summary>
    /// Maps a pointer position to normalized image coordinates, accounting for the letterboxing
    /// that Stretch=Uniform applies when the image aspect ratio differs from the control's.
    /// </summary>
    private (double xn, double yn) ToNormalized(Point pos)
    {
        double bw = Viewer.Bounds.Width, bh = Viewer.Bounds.Height;
        if (bw <= 0 || bh <= 0) return (0, 0);

        double pw = 0, ph = 0;
        if (Viewer.Source is IImage img)
        {
            var size = img.Size;
            pw = size.Width;
            ph = size.Height;
        }
        if (pw <= 0 || ph <= 0) return (Clamp01(pos.X / bw), Clamp01(pos.Y / bh));

        double scale = Math.Min(bw / pw, bh / ph);
        double offX = (bw - pw * scale) / 2.0;
        double offY = (bh - ph * scale) / 2.0;
        return (Clamp01((pos.X - offX) / (pw * scale)), Clamp01((pos.Y - offY) / (ph * scale)));
    }

    private void OnViewerPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_connected || _session is null) return;
        var (xn, yn) = ToNormalized(e.GetPosition(Viewer));
        _ = _session.SendAsync(new ClientToServer { Input = InputEncoder.Move(xn, yn) }, _cts!.Token);
    }

    private void OnViewerPointerButton(object? sender, PointerEventArgs e)
    {
        if (!_connected || _session is null) return;
        var props = e.GetCurrentPoint(Viewer).Properties;
        var btn = MapButton(props);
        if (btn is null) return;
        bool down = e.RoutedEvent == PointerPressedEvent;
        var (xn, yn) = ToNormalized(e.GetPosition(Viewer));
        var input = down ? InputEncoder.ButtonDown(btn.Value, xn, yn)
                         : InputEncoder.ButtonUp(btn.Value, xn, yn);
        _ = _session.SendAsync(new ClientToServer { Input = input }, _cts!.Token);
    }

    private void OnViewerPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!_connected || _session is null) return;
        double delta = e.Delta.Y > 0 ? 1 : -1;
        _ = _session.SendAsync(new ClientToServer { Input = InputEncoder.Scroll(delta) }, _cts!.Token);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e) => SendKey(e, down: true);
    private void OnKeyUp(object? sender, KeyEventArgs e) => SendKey(e, down: false);

    private void SendKey(KeyEventArgs e, bool down)
    {
        if (!_connected || _session is null) return;
        // Don't forward keys typed into the address box (or other text fields) to the host.
        if (FocusManager?.GetFocusedElement() is TextBox) return;

        var (vk, sc, ext) = InputEncoder.MapKey(e.Key);
        if (vk == 0) return;
        e.Handled = true;
        _ = _session.SendAsync(new ClientToServer { Input = InputEncoder.KeyPress(vk, sc, down, ext) }, _cts!.Token);
    }

    private static uint? MapButton(PointerPointProperties props)
    {
        if (props.IsLeftButtonPressed) return 0;
        if (props.IsRightButtonPressed) return 1;
        if (props.IsMiddleButtonPressed) return 2;
        return null;
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
