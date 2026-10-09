using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PhoneCam.Desktop.Hosting;
using PhoneCam.Desktop.Video;

namespace PhoneCam.Desktop;

public partial class MainWindow : Window
{
    private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x7E, 0xE0, 0xA1));
    private static readonly Brush Pending = new SolidColorBrush(Color.FromRgb(0xD2, 0xA9, 0x57));
    private static readonly Brush Failed = new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x88));
    private static readonly Brush Idle = new SolidColorBrush(Color.FromRgb(0x89, 0x91, 0x9D));

    private readonly PhoneCamServer _server = new();
    private readonly MediaMtxApi _api = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _networkRefresh = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly SlotFeed _iphoneFeed;
    private IReadOnlyList<NetworkCandidate> _candidates = [];
    private IphoneUsbStatus? _usb;
    private UsbAction _usbAction;
    private bool _probingUsb;
    private PreviewWindow? _preview;
    private bool _publishing;
    private bool _polling;
    private bool _shutdownComplete;
    private (DateTime At, long Bytes, long Frames)? _lastSample;

    public MainWindow()
    {
        InitializeComponent();
        _iphoneFeed = new SlotFeed(PhoneCamServer.RtspUrl(Slots.Iphone), cameraIndex: 0, "PhoneCam iPhone");
        _server.StateChanged += () => Dispatcher.InvokeAsync(OnServerStateChanged);
        _server.OrientationChanged += slot => Dispatcher.InvokeAsync(() =>
        {
            if (slot == Slots.Iphone) _iphoneFeed.SetRotation(_server.GetRotation(slot));
        });
        _statusTimer.Tick += async (_, _) =>
        {
            await PollSlotAsync();
            await RefreshUsbAsync();
        };
        _networkRefresh.Tick += (_, _) =>
        {
            _networkRefresh.Stop();
            LoadAddresses(force: false);
        };
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await EnsureSystemSetupAsync();
        LoadAddresses();
        _statusTimer.Start();
    }

    // ---------- Network & server ----------

    /// <param name="force">False when triggered by a network change: an unchanged list keeps the running server untouched.</param>
    private void LoadAddresses(bool force = true)
    {
        IReadOnlyList<NetworkCandidate> candidates = NetworkCandidates.Discover();
        NetworkCandidate? current = AddressPicker.SelectedItem as NetworkCandidate;
        if (!force && current is not null && candidates.SequenceEqual(_candidates)) return;

        // Plugging the iPhone in with Personal Hotspot on means "use the cable"; switch unless that would cut a live stream.
        NetworkCandidate? usb = candidates.FirstOrDefault(c => c.IsIphoneUsb);
        bool usbAppeared = usb is not null && !_candidates.Any(c => c.IsIphoneUsb);
        _candidates = candidates;
        NetworkCandidate? choice =
            (usbAppeared && !_publishing ? usb : null)
            ?? candidates.FirstOrDefault(c => current is not null && c.InterfaceId == current.InterfaceId && c.Address.Equals(current.Address))
            ?? candidates.FirstOrDefault(c => c.InterfaceId == _settings.PreferredInterfaceId)
            ?? candidates.FirstOrDefault();
        AddressPicker.ItemsSource = candidates;
        if (choice is null)
        {
            SetServerStatus(Failed, "Brak sieci");
            ShowNetworkWarning("Komputer nie ma aktywnego połączenia IPv4. Połącz go z tą samą siecią Wi-Fi co telefon i wybierz „Odśwież”.");
            return;
        }
        AddressPicker.SelectedItem = choice;
    }

    private async void AddressPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AddressPicker.SelectedItem is not NetworkCandidate candidate) return;
        if (_server.Address?.Equals(candidate.Address) == true && _server.State is ServerState.Running or ServerState.Starting) return;

        _settings.PreferredInterfaceId = candidate.InterfaceId;
        _settings.Save();
        // The USB hotspot network is usually public; PhoneCam's USB firewall rules cover it.
        ShowNetworkWarning(candidate.IsPublicNetwork == true && !candidate.IsIphoneUsb
            ? "Ta sieć jest w Windows oznaczona jako publiczna, a reguły zapory PhoneCam działają tylko w sieciach prywatnych. " +
              "Zmień ją na prywatną: Ustawienia → Sieć i Internet → właściwości sieci → Prywatna."
            : !NetworkCandidates.IsPrivate(candidate.Address)
                ? "Wybrany adres nie należy do typowej sieci domowej (10.x, 172.16–31.x, 192.168.x). Certyfikat PhoneCam działa tylko dla takich adresów."
                : null);
        AddressPicker.IsEnabled = false;
        try { await _server.StartAsync(candidate.Address); }
        finally { AddressPicker.IsEnabled = true; }
    }

    private void RefreshAddresses_Click(object sender, RoutedEventArgs e) => LoadAddresses();

    // Raised on a thread-pool thread, often several times per change (DHCP, IPv6): debounce, then re-read.
    private void OnNetworkAddressChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        _networkRefresh.Stop();
        _networkRefresh.Start();
    });

    private bool UsingUsb => _usb?.Address is { } usb && usb.Equals(_server.Address);

    // ---------- iPhone over USB ----------

    private enum UsbAction { None, InstallDriver, SwitchToUsb, StopInternet }

    private async Task RefreshUsbAsync()
    {
        if (_probingUsb) return;
        _probingUsb = true;
        try { _usb = await Task.Run(IphoneUsb.Probe); }
        catch (NetworkInformationException) { return; }
        finally { _probingUsb = false; }
        UpdateUsbPanel(_usb);
    }

    private void UpdateUsbPanel(IphoneUsbStatus usb)
    {
        const string EnableHotspot = "Ustawienia → Osobisty hotspot → Pozwalaj innym na łączenie";
        (Brush dot, string title, string detail, UsbAction action) = usb switch
        {
            _ when UsingUsb && usb.CarriesInternet => (Pending, "Kabel USB · aktywny",
                "Windows wysyła też internet komputera przez iPhone'a, czyli przez dane komórkowe.", UsbAction.StopInternet),
            _ when UsingUsb => (Ok, "Kabel USB · aktywny",
                "Kody QR prowadzą przez kabel. Telefon nie potrzebuje Wi-Fi.", UsbAction.None),
            { Address: not null } => (Pending, "Kabel USB · dostępny",
                _publishing ? "Obraz idzie teraz przez Wi-Fi. Po przełączeniu zeskanuj ponownie kod 2." : "iPhone jest podłączony kablem z włączonym hotspotem.", UsbAction.SwitchToUsb),
            { AdapterPresent: true } => (Pending, "Kabel USB · brak łącza",
                $"Upewnij się, że na iPhonie jest włączone: {EnableHotspot} (i dane komórkowe), potem odłącz i podłącz kabel.", UsbAction.None),
            { TetherFunction: true } => (Failed, "Kabel USB · brak sterownika sieci Apple",
                "iPhone udostępnia sieć, ale Windows nie ma sterownika „Apple Mobile Device Ethernet” (Apple Devices go nie zawiera).", UsbAction.None),
            { PhoneConnected: true, AppleSoftware: false } => (Failed, "Kabel USB · brak oprogramowania Apple",
                "Windows widzi iPhone'a tylko jako aparat. Zainstaluj Apple Devices, potem odłącz i podłącz kabel.", UsbAction.InstallDriver),
            { PhoneConnected: true } => (Pending, "Kabel USB · iPhone podłączony",
                $"Na iPhonie wybierz „Zaufaj” i włącz: {EnableHotspot}, potem odłącz i podłącz kabel.", UsbAction.None),
            _ => (Idle, "Kabel USB · niższe opóźnienie",
                $"Włącz na iPhonie: {EnableHotspot}, potem podłącz kabel (wymaga Apple Devices).", UsbAction.None)
        };
        UsbDot.Foreground = dot;
        UsbTitle.Text = title;
        UsbDetail.Text = detail;
        _usbAction = action;
        UsbActionButton.Content = action switch
        {
            UsbAction.InstallDriver => "Pobierz Apple Devices",
            UsbAction.SwitchToUsb => "Przełącz na USB",
            UsbAction.StopInternet => "Nie używaj internetu iPhone'a",
            _ => ""
        };
        UsbActionButton.Visibility = action == UsbAction.None ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void UsbAction_Click(object sender, RoutedEventArgs e)
    {
        switch (_usbAction)
        {
            case UsbAction.InstallDriver:
                try { Process.Start(new ProcessStartInfo(IphoneUsb.AppleDevicesStoreUri) { UseShellExecute = true }); }
                catch (Win32Exception) // no Microsoft Store app: fall back to the web listing
                {
                    Process.Start(new ProcessStartInfo("https://apps.microsoft.com/detail/9NP83LWLPZ9K") { UseShellExecute = true });
                }
                break;
            case UsbAction.SwitchToUsb:
                LoadAddresses(force: false);
                if (_candidates.FirstOrDefault(c => c.IsIphoneUsb) is { } usb) AddressPicker.SelectedItem = usb;
                break;
            case UsbAction.StopInternet when _usb?.InterfaceIndex is int index:
                UsbActionButton.IsEnabled = false;
                try { await IphoneUsb.StopCarryingInternetAsync(index); }
                finally { UsbActionButton.IsEnabled = true; }
                await RefreshUsbAsync();
                break;
        }
    }

    private void OnServerStateChanged()
    {
        switch (_server.State)
        {
            case ServerState.Starting:
                SetServerStatus(Pending, "Uruchamianie serwera…");
                break;
            case ServerState.Running:
                SetServerStatus(Ok, $"Serwer działa · {_server.Address}");
                UpdateQrCodes();
                break;
            case ServerState.Failed:
                SetServerStatus(Failed, "Błąd serwera");
                ShowNetworkWarning(_server.Error);
                break;
            default:
                SetServerStatus(Idle, "Serwer zatrzymany");
                break;
        }
        ConnectIosButton.IsEnabled = _server.State == ServerState.Running;
    }

    private void SetServerStatus(Brush color, string text)
    {
        ServerDot.Foreground = color;
        ServerStatus.Text = text;
    }

    private void ShowNetworkWarning(string? text)
    {
        NetworkWarning.Text = text ?? "";
        NetworkWarning.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Firewall rules and the virtual camera registration need administrator rights: one UAC prompt for whatever is missing.</summary>
    private async Task EnsureSystemSetupAsync()
    {
        (bool firewall, bool camera) = await Task.Run(CheckSystemSetup);
        if (!firewall || !camera)
        {
            await Elevated.RunScriptAsync("setup.cmd",
                (firewall ? "" : FirewallRules.SetupCommands()) + (camera ? "" : VirtualCameraRegistration.SetupCommands()));
            (firewall, camera) = await Task.Run(CheckSystemSetup);
        }
        SetupWarning.Text = (firewall, camera) switch
        {
            (false, false) => "Brakuje reguł zapory i rejestracji kamery wirtualnej. Bez nich telefon może się nie połączyć, a kamera nie pojawi się w OBS ani Teams.",
            (false, _) => "Zapora Windows nie ma reguł PhoneCam. Bez nich telefon może nie połączyć się z komputerem.",
            _ => "Kamera wirtualna nie jest zarejestrowana, więc nie pojawi się w OBS ani Teams."
        };
        FirewallPanel.Visibility = firewall && camera ? Visibility.Collapsed : Visibility.Visible;
    }

    // A missing DLL (Visual Studio C++ tools absent at build time) is not something UAC can fix; the status line says so.
    private static (bool Firewall, bool Camera) CheckSystemSetup() =>
        (FirewallRules.AreConfigured(), !VirtualCameraRegistration.DllPresent || VirtualCameraRegistration.IsRegistered());

    private async void ConfigureSystem_Click(object sender, RoutedEventArgs e) => await EnsureSystemSetupAsync();

    // ---------- Navigation ----------

    private void ConnectIos_Click(object sender, RoutedEventArgs e)
    {
        UpdateQrCodes();
        HomeView.Visibility = Visibility.Collapsed;
        IosView.Visibility = Visibility.Visible;
        ConnectionCard.Visibility = Visibility.Visible;
    }

    private void BackToHome_Click(object sender, RoutedEventArgs e)
    {
        IosView.Visibility = Visibility.Collapsed;
        HomeView.Visibility = Visibility.Visible;
    }

    private void UpdateQrCodes()
    {
        if (_server.State != ServerState.Running) return;
        CertificateUrl.Text = _server.CertificateUrl;
        CertificateQr.Source = QrCodes.Create(_server.CertificateUrl);
        string publishUrl = _server.PublishUrl(Slots.Iphone);
        PublishUrl.Text = publishUrl;
        PublishQr.Source = QrCodes.Create(publishUrl);
    }

    // ---------- Connection status & preview ----------

    private async Task PollSlotAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            SlotStatus? status = _server.State == ServerState.Running ? await _api.GetSlotAsync(Slots.Iphone) : null;
            UpdateConnection(status);
        }
        finally { _polling = false; }
    }

    private void UpdateConnection(SlotStatus? status)
    {
        bool publishing = status?.Publishing == true;
        if (publishing != _publishing)
        {
            _publishing = publishing;
            _lastSample = null;
            _iphoneFeed.SetPublishing(publishing);
            if (publishing)
            {
                ConnectionCard.Visibility = Visibility.Visible;
                OpenPreview();
            }
        }

        ShowPreviewButton.Visibility = publishing && _preview is null ? Visibility.Visible : Visibility.Collapsed;
        CameraStatus.Text = DescribeCamera();

        if (status is null)
        {
            ConnectionDot.Foreground = _server.State == ServerState.Running ? Failed : Idle;
            ConnectionStatus.Text = _server.State == ServerState.Running ? "iPhone · serwer nie odpowiada" : "iPhone · serwer nie działa";
            ConnectionDetails.Text = "";
            return;
        }
        if (!publishing)
        {
            ConnectionDot.Foreground = Idle;
            ConnectionStatus.Text = "iPhone · oczekiwanie na połączenie";
            ConnectionDetails.Text = "Zeskanuj kod 2 i wybierz na telefonie „Uruchom kamerę i połącz”.";
            return;
        }

        ConnectionDot.Foreground = Ok;
        ConnectionStatus.Text = $"iPhone · połączono przez {(UsingUsb ? "kabel USB" : "Wi-Fi")}, obraz odbierany";
        ConnectionDetails.Text = string.Join("  ·  ", DescribeStream(status));
    }

    private IEnumerable<string> DescribeStream(SlotStatus status)
    {
        if (status.Tracks.Count > 0) yield return string.Join("+", status.Tracks);
        if (_iphoneFeed.SourceSize is { } size)
        {
            // Source as sent by Safari; the output is always 16:9 (rotated per the phone, portrait centre-cropped).
            int rotation = _iphoneFeed.Rotation;
            bool portraitAfterRotation = (rotation % 180 == 0) == (size.Height > size.Width);
            yield return $"{size.Width}×{size.Height}"
                + (rotation != 0 ? $" ↻{rotation}°" : "")
                + (portraitAfterRotation ? " (kadr 16:9 z pionu)" : "");
        }

        DateTime now = DateTime.UtcNow;
        long frames = _iphoneFeed.DecodedFrames;
        if (_lastSample is { } last && (now - last.At).TotalSeconds > 0.2)
        {
            double seconds = (now - last.At).TotalSeconds;
            if (frames >= last.Frames)
                yield return $"{(frames - last.Frames) / seconds:0} fps";
            yield return $"{(status.BytesReceived - last.Bytes) * 8 / seconds / 1_000_000:0.0} Mb/s";
        }
        _lastSample = (now, status.BytesReceived, frames);

        if (status.PublishingSince is { } since)
            yield return $"{DateTimeOffset.Now - since:hh\\:mm\\:ss}";
    }

    private void OpenPreview()
    {
        if (_preview is not null)
        {
            _preview.Activate();
            return;
        }
        _preview = new PreviewWindow("PhoneCam · iPhone", _iphoneFeed);
        _preview.Closed += (_, _) => _preview = null;
        _preview.Show();
    }

    private void ShowPreview_Click(object sender, RoutedEventArgs e) => OpenPreview();

    private string DescribeCamera()
    {
        const string Name = "Kamera „PhoneCam iPhone”";
        if (!VirtualCameraRegistration.DllPresent)
            return $"{Name}: niedostępna — brak PhoneCam.VirtualCamera.dll (build wymaga Visual Studio z C++).";
        if (!VirtualCameraRegistration.IsRegistered())
            return $"{Name}: niezarejestrowana — wybierz „Skonfiguruj” u góry okna.";
        if (!_iphoneFeed.CameraAvailable)
            return $"{Name}: zajęta przez poprzednią sesję w innej aplikacji, ponawiam…";
        return _iphoneFeed.CameraInUse
            ? $"{Name}: używana przez aplikację (OBS, Teams…)."
            : $"{Name}: gotowa — w OBS dodaj „Urządzenie przechwytywania wideo” i wybierz „PhoneCam iPhone”.";
    }

    // ---------- Shutdown ----------

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true;
        IsEnabled = false;
        _statusTimer.Stop();
        _networkRefresh.Stop();
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _preview?.Close();
        await _iphoneFeed.DisposeAsync();
        await _server.DisposeAsync();
        _api.Dispose();
        _shutdownComplete = true;
        Close();
    }
}
