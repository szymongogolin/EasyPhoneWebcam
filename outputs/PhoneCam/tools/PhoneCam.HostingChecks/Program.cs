using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PhoneCam.Desktop.Hosting;
using PhoneCam.Desktop.Video;

// End-to-end checks of the desktop host without the WPF window: local PKI, Kestrel page server,
// MediaMTX (config, TLS, API) and RTSP preview decoding. Uses a throwaway data directory and CA key,
// and never touches firewall rules. Requires a private LAN IPv4 address and free PhoneCam ports.

string dataDir = Path.Combine(Path.GetTempPath(), "PhoneCam.HostingChecks", Guid.NewGuid().ToString("N"));
AppPaths.Root = Directory.CreateDirectory(dataDir).FullName;
LocalCertificates.CaKeyName = "PhoneCam Local CA (checks)";
AppPaths.MediaMtxExe = FindBundledMediaMtx() ?? AppPaths.MediaMtxExe;
string ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "win-x64", "ffmpeg.exe");

IPAddress? lan = NetworkCandidates.Discover().Select(c => c.Address).FirstOrDefault(NetworkCandidates.IsPrivate);
if (lan is null)
{
    Console.Error.WriteLine("No private LAN IPv4 address; connect the PC to a network first.");
    return 2;
}
Console.WriteLine($"LAN address under test: {lan}");

int passed = 0, failed = 0;
async Task Check(string name, Func<Task> body)
{
    try { await body(); passed++; Console.WriteLine($"PASS  {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL  {name}: {ex.Message}"); }
}
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

X509Certificate2 ca = null!;
ServerCertificate server = null!;
var web = new PhoneWebServer();
var mediaMtx = new MediaMtxProcess();
using var api = new MediaMtxApi();
var publishers = new List<Process>();
(string Slot, int Rotation)? reportedOrientation = null;

try
{
    await Check("root CA is a CA limited by critical name constraints", () =>
    {
        server = LocalCertificates.EnsureServerCertificate(lan);
        ca = X509CertificateLoader.LoadCertificateFromFile(LocalCertificates.RootCaPath);
        Assert(ca.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority, "not a CA");
        X509Extension? constraints = ca.Extensions["2.5.29.30"];
        Assert(constraints is { Critical: true }, "name constraints missing or not critical");
        return Task.CompletedTask;
    });

    await Check("server certificate chains to the CA for the LAN IP and is valid ≤ 825 days", () =>
    {
        Assert(BuildChain(server.Certificate, ca, out string status), status);
        Assert(server.Certificate.HasPrivateKey, "no private key for Kestrel");
        Assert((server.Certificate.NotAfter - server.Certificate.NotBefore).TotalDays <= 825, "validity too long for iOS");
        return Task.CompletedTask;
    });

    await Check("name constraints reject a certificate for a public IP", () =>
    {
        byte[] caBefore = File.ReadAllBytes(LocalCertificates.RootCaPath);
        using X509Certificate2 rogue = LocalCertificates.EnsureServerCertificate(IPAddress.Parse("8.8.8.8")).Certificate;
        Assert(!BuildChain(rogue, ca, out string status), "public IP certificate was accepted");
        Assert(status.Contains("NameConstraint", StringComparison.OrdinalIgnoreCase), $"rejected for the wrong reason: {status}");
        Assert(File.ReadAllBytes(LocalCertificates.RootCaPath).AsSpan().SequenceEqual(caBefore), "CA changed while reissuing the server certificate");
        server = LocalCertificates.EnsureServerCertificate(lan);
        return Task.CompletedTask;
    });

    await Check("server certificate is reused while the IP is unchanged", () =>
    {
        string thumbprint = server.Certificate.Thumbprint;
        server = LocalCertificates.EnsureServerCertificate(lan);
        Assert(server.Certificate.Thumbprint == thumbprint, "certificate was reissued unnecessarily");
        return Task.CompletedTask;
    });

    await Check("iPhone USB hotspot address 172.20.10.2 gets a certificate valid under the name constraints", () =>
    {
        using X509Certificate2 usb = LocalCertificates.EnsureServerCertificate(IPAddress.Parse("172.20.10.2")).Certificate;
        Assert(BuildChain(usb, ca, out string status), status);
        server = LocalCertificates.EnsureServerCertificate(lan);
        return Task.CompletedTask;
    });

    await Check("iPhone USB detection: PnP device list and route lookup work without a phone", () =>
    {
        IReadOnlyList<string> usbDevices = IphoneUsb.PresentDeviceIds("USB");
        Assert(usbDevices.Count > 0 && usbDevices.All(id => id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)), $"unexpected USB device list ({usbDevices.Count} entries)");
        NetworkInterface nic = NetworkInterface.GetAllNetworkInterfaces().First(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(lan)));
        IPInterfaceProperties properties = nic.GetIPProperties();
        IPAddress? gateway = properties.GatewayAddresses.Select(g => g.Address).FirstOrDefault(g => g.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        if (gateway is not null)
        {
            int? best = IphoneUsb.BestInterfaceIndex(gateway);
            Assert(best == properties.GetIPv4Properties().Index, $"route to {gateway} uses interface {best}, expected {properties.GetIPv4Properties().Index}");
        }
        IphoneUsbStatus status = IphoneUsb.Probe();
        Assert(status.Address is null || status.AdapterPresent, "USB address reported without the Apple adapter");
        Console.WriteLine($"      {status}");
        return Task.CompletedTask;
    });

    using HttpClient https = TrustingOnly(ca);
    using var http = new HttpClient();

    await Check("web server: CA over HTTP 8787, phone page over HTTPS 8443 only", async () =>
    {
        await web.StartAsync(server.Certificate, (slot, rotation) => reportedOrientation = (slot, rotation));
        HttpResponseMessage caResponse = await http.GetAsync($"http://{lan}:{Ports.CertificateDownload}/PhoneCam-RootCA.cer");
        Assert(caResponse.IsSuccessStatusCode, $"CA download HTTP {(int)caResponse.StatusCode}");
        Assert(caResponse.Content.Headers.ContentType?.MediaType == "application/x-x509-ca-cert", "wrong CA content type");
        Assert((await caResponse.Content.ReadAsByteArrayAsync()).AsSpan().SequenceEqual(ca.RawData), "CA bytes differ");

        HttpResponseMessage page = await https.GetAsync($"https://{lan}:{Ports.PhonePage}/{Slots.Iphone}");
        Assert(page.IsSuccessStatusCode && (await page.Content.ReadAsStringAsync()).Contains("RTCPeerConnection"), $"page HTTP {(int)page.StatusCode}");
        Assert((await https.GetAsync($"https://{lan}:{Ports.PhonePage}/unknown")).StatusCode == HttpStatusCode.NotFound, "unknown slot served");
        Assert((await http.GetAsync($"http://{lan}:{Ports.CertificateDownload}/{Slots.Iphone}")).StatusCode == HttpStatusCode.NotFound, "page served over plain HTTP");
    });

    await Check("MediaMTX starts, API reports idle slot, WHIP answers over trusted TLS", async () =>
    {
        mediaMtx.Start(lan, server);
        SlotStatus? status = null;
        for (int i = 0; i < 30 && status is null; i++) { await Task.Delay(200); status = await api.GetSlotAsync(Slots.Iphone); }
        Assert(mediaMtx.IsRunning, $"MediaMTX exited: {mediaMtx.LastError}");
        Assert(status is { Publishing: false }, "API unreachable or slot unexpectedly publishing");
        using var options = new HttpRequestMessage(HttpMethod.Options, $"https://{lan}:{Ports.Whip}/{Slots.Iphone}/whip");
        options.Headers.Add("Origin", $"https://{lan}:{Ports.PhonePage}");
        HttpResponseMessage whip = await https.SendAsync(options);
        Assert(whip.IsSuccessStatusCode, $"WHIP OPTIONS HTTP {(int)whip.StatusCode}");
    });

    await Check("published landscape stream: API shows H264, preview decodes 1280×720 frames", async () =>
    {
        publishers.Add(Publish(ffmpeg, Slots.Iphone, "testsrc2=size=1280x720:rate=30"));
        SlotStatus status = await WaitForPublishing(api, Slots.Iphone);
        Assert(status.Tracks.Any(t => t.Contains("264")), $"tracks: {string.Join(",", status.Tracks)}");
        await using var decoder = new FfmpegStreamDecoder(PhoneCamServer.RtspUrl(Slots.Iphone), 1280, 720);
        decoder.Start();
        DecodedVideoFrame frame = await WaitForFrames(decoder, 10);
        Assert(decoder.SourceSize == (1280, 720), $"source size {decoder.SourceSize}");
        Assert(frame.Bgr24.AsSpan().IndexOfAnyExcept(frame.Bgr24[0]) >= 0, "uniform frame");
        SlotStatus later = (await api.GetSlotAsync(Slots.Iphone))!;
        Assert(later.BytesReceived > status.BytesReceived, "bytesReceived does not grow");
    });

    // Portrait test source, as Safari sends it under the iOS rotation lock: blue, with a red band along the
    // top edge of the frame (the phone's top edge). Lets the checks tell crop and rotation direction apart.
    publishers.Add(Publish(ffmpeg, Slots.Android, "color=c=blue:s=720x1280:r=30,drawbox=x=0:y=0:w=720:h=200:color=red:t=fill"));

    await Check("portrait stream without rotation fills 16:9 by centre crop (no bars)", async () =>
    {
        await WaitForPublishing(api, Slots.Android);
        await using var decoder = new FfmpegStreamDecoder(PhoneCamServer.RtspUrl(Slots.Android), 1280, 720);
        decoder.Start();
        DecodedVideoFrame frame = await WaitForFrames(decoder, 5);
        Assert(decoder.SourceSize == (720, 1280), $"source size {decoder.SourceSize}");
        foreach ((int x, int y) in new[] { (10, 10), (10, 360), (1270, 360), (640, 710) })
            Assert(IsBlue(Pixel(frame, x, y)), $"pixel {x},{y} is {Pixel(frame, x, y)}, expected blue (bars or red band left in the crop)");
    });

    await Check("rotation 90° turns the phone's top edge to the left, landscape without crop", async () =>
    {
        await using var decoder = new FfmpegStreamDecoder(PhoneCamServer.RtspUrl(Slots.Android), 1280, 720, rotation: 90);
        decoder.Start();
        DecodedVideoFrame frame = await WaitForFrames(decoder, 5);
        Assert(IsRed(Pixel(frame, 40, 360)), $"left edge is {Pixel(frame, 40, 360)}, expected red");
        Assert(IsBlue(Pixel(frame, 1240, 360)), $"right edge is {Pixel(frame, 1240, 360)}, expected blue");
        Assert(IsBlue(Pixel(frame, 640, 360)), "centre is not blue");
    });

    await Check("rotation 270° turns the phone's top edge to the right", async () =>
    {
        await using var decoder = new FfmpegStreamDecoder(PhoneCamServer.RtspUrl(Slots.Android), 1280, 720, rotation: 270);
        decoder.Start();
        DecodedVideoFrame frame = await WaitForFrames(decoder, 5);
        Assert(IsRed(Pixel(frame, 1240, 360)), $"right edge is {Pixel(frame, 1240, 360)}, expected red");
        Assert(IsBlue(Pixel(frame, 40, 360)), $"left edge is {Pixel(frame, 40, 360)}, expected blue");
    });

    await Check("phone orientation endpoint validates input and notifies the host", async () =>
    {
        HttpResponseMessage ok = await https.PostAsync($"https://{lan}:{Ports.PhonePage}/api/orientation/{Slots.Iphone}",
            new StringContent("""{"rotation":270}""", System.Text.Encoding.UTF8, "application/json"));
        Assert(ok.StatusCode == HttpStatusCode.NoContent, $"HTTP {(int)ok.StatusCode}");
        Assert(reportedOrientation == (Slots.Iphone, 270), $"host received {reportedOrientation}");
        HttpResponseMessage bad = await https.PostAsync($"https://{lan}:{Ports.PhonePage}/api/orientation/{Slots.Iphone}",
            new StringContent("""{"rotation":45}""", System.Text.Encoding.UTF8, "application/json"));
        Assert(bad.StatusCode == HttpStatusCode.BadRequest, $"rotation 45 accepted: HTTP {(int)bad.StatusCode}");
    });

    await Check("stopping a publisher returns the slot to idle", async () =>
    {
        publishers[0].Kill();
        SlotStatus? status = null;
        for (int i = 0; i < 25; i++) { await Task.Delay(200); status = await api.GetSlotAsync(Slots.Iphone); if (status is { Publishing: false }) break; }
        Assert(status is { Publishing: false }, "slot still publishing");
    });

    // Virtual camera: needs the DirectShow filter registered (PhoneCam does that once through UAC) and no
    // running PhoneCam (one sender per camera). FFmpeg's dshow input stands in for OBS/Teams.
    const string CameraName = "PhoneCam iPhone";

    await Check("virtual camera: a DirectShow client gets the decoded frames upright and unmirrored", async () =>
    {
        Assert(await DirectShowDevices(ffmpeg) is var devices && devices.Contains($"\"{CameraName}\""),
            $"„{CameraName}” is not a DirectShow device; start PhoneCam once to register it (UAC)");
        using VirtualCamera camera = VirtualCamera.TryCreate(0)
            ?? throw new InvalidOperationException("camera frame buffer is busy (is PhoneCam running?)");
        // Landscape source with a red band along the top and a green block top-left: a flip or mirror moves them.
        publishers.Add(Publish(ffmpeg, Slots.Iphone,
            "color=c=blue:s=1280x720:r=30,drawbox=x=0:y=0:w=1280:h=144:color=red:t=fill,drawbox=x=0:y=0:w=200:h=144:color=lime:t=fill"));
        await WaitForPublishing(api, Slots.Iphone);
        await using var decoder = new FfmpegStreamDecoder(PhoneCamServer.RtspUrl(Slots.Iphone), VirtualCamera.Width, VirtualCamera.Height);
        decoder.FrameDecoded += frame => camera.Send(frame.Bgr24);
        decoder.Start();
        await WaitForFrames(decoder, 5);

        DecodedVideoFrame captured = await CaptureFromCamera(ffmpeg, CameraName);
        Assert(IsRed(Pixel(captured, 640, 40)), $"top centre is {Pixel(captured, 640, 40)}, expected red");
        Assert(IsGreen(Pixel(captured, 60, 40)), $"top left is {Pixel(captured, 60, 40)}, expected green (mirrored?)");
        Assert(IsBlue(Pixel(captured, 640, 600)), $"bottom is {Pixel(captured, 640, 600)}, expected blue (flipped?)");
        Assert(camera.InUse, "camera does not report the connected client");
    });

    await Check("slot feed: camera shows the no-signal card while idle, live frames while publishing", async () =>
    {
        // The card is rendered with WPF, which needs an STA thread (PhoneCam builds the feed on its UI thread).
        SlotFeed feed = await OnStaThread(() => new SlotFeed(PhoneCamServer.RtspUrl(Slots.Iphone), 0, CameraName));
        try
        {
            Assert(feed.CameraAvailable, "feed could not create the camera");
            DecodedVideoFrame idle = await CaptureFromCamera(ffmpeg, CameraName);
            (byte b, byte g, byte r) = Pixel(idle, 10, 10);
            Assert(Math.Abs(b - 0x20) < 12 && Math.Abs(g - 0x1A) < 12 && Math.Abs(r - 0x17) < 12, $"idle corner is {(b, g, r)}, expected the card background");
            Assert(idle.Bgr24.AsSpan(idle.Width * 3 * 300, idle.Width * 3 * 120).IndexOfAnyInRange((byte)200, (byte)255) >= 0, "no card text in the middle");

            feed.SetPublishing(true);
            for (int i = 0; i < 100 && feed.DecodedFrames < 10; i++) await Task.Delay(100);
            DecodedVideoFrame live = await CaptureFromCamera(ffmpeg, CameraName);
            Assert(IsRed(Pixel(live, 640, 40)) && IsBlue(Pixel(live, 640, 600)), $"live frame top {Pixel(live, 640, 40)}, bottom {Pixel(live, 640, 600)}");
        }
        finally { await feed.DisposeAsync(); }
    });

    await Check("virtual camera opens at 1280×720 while PhoneCam sends nothing", async () =>
    {
        DecodedVideoFrame captured = await CaptureFromCamera(ffmpeg, CameraName);
        Assert(captured.Bgr24.Average(b => b) < 80, "frame without a sender is not dark");
    });
}
finally
{
    foreach (Process p in publishers) { try { p.Kill(); } catch (InvalidOperationException) { } p.Dispose(); }
    await mediaMtx.DisposeAsync();
    await web.DisposeAsync();
    if (CngKey.Exists(LocalCertificates.CaKeyName)) CngKey.Open(LocalCertificates.CaKeyName).Delete();
    try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { }
}

Console.WriteLine($"{passed}/{passed + failed} hosting checks passed.");
return failed == 0 ? 0 : 1;

static bool BuildChain(X509Certificate2 certificate, X509Certificate2 root, out string status)
{
    using var chain = new X509Chain();
    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    chain.ChainPolicy.CustomTrustStore.Add(root);
    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
    bool ok = chain.Build(certificate);
    status = string.Join(", ", chain.ChainStatus.Select(s => s.Status));
    return ok;
}

static HttpClient TrustingOnly(X509Certificate2 root) => new(new SocketsHttpHandler
{
    SslOptions =
    {
        RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            certificate is not null
            && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0
            && BuildChain(X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()), root, out _)
    }
});

static Process Publish(string ffmpeg, string slot, string lavfiSource)
{
    var startInfo = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
    foreach (string a in new[]
    {
        "-hide_banner", "-nostdin", "-loglevel", "error", "-re", "-f", "lavfi", "-i", lavfiSource,
        "-t", "30", "-pix_fmt", "yuv420p", "-c:v", "libopenh264", "-g", "30", "-f", "rtsp", "-rtsp_transport", "tcp",
        PhoneCamServer.RtspUrl(slot)
    }) startInfo.ArgumentList.Add(a);
    Process process = Process.Start(startInfo)!;
    process.ErrorDataReceived += (_, _) => { };
    process.BeginErrorReadLine();
    return process;
}

static async Task<SlotStatus> WaitForPublishing(MediaMtxApi api, string slot)
{
    for (int i = 0; i < 50; i++)
    {
        if (await api.GetSlotAsync(slot) is { Publishing: true } status) return status;
        await Task.Delay(200);
    }
    throw new TimeoutException($"slot {slot} never became ready");
}

static async Task<DecodedVideoFrame> WaitForFrames(FfmpegStreamDecoder decoder, int count)
{
    var deadline = DateTime.UtcNow.AddSeconds(15);
    DecodedVideoFrame? last = null;
    while (DateTime.UtcNow < deadline)
    {
        if (decoder.TakeLatest() is { } frame) last = frame;
        if (last is not null && decoder.DecodedFrames >= count) return last;
        await Task.Delay(50);
    }
    throw new TimeoutException($"decoded {decoder.DecodedFrames} frames; ffmpeg: {decoder.LastError ?? "no error"}");
}

static string? FindBundledMediaMtx()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        string desktopBin = Path.Combine(dir.FullName, "src", "PhoneCam.Desktop", "bin");
        if (Directory.Exists(desktopBin))
            return Directory.EnumerateFiles(desktopBin, "mediamtx.exe", SearchOption.AllDirectories).FirstOrDefault();
    }
    return null;
}

static (byte B, byte G, byte R) Pixel(DecodedVideoFrame frame, int x, int y)
{
    int i = (y * frame.Width + x) * 3;
    return (frame.Bgr24[i], frame.Bgr24[i + 1], frame.Bgr24[i + 2]);
}

static Task<T> OnStaThread<T>(Func<T> create)
{
    var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        try { result.SetResult(create()); }
        catch (Exception ex) { result.SetException(ex); }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    return result.Task;
}

static async Task<string> DirectShowDevices(string ffmpeg)
{
    var startInfo = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
    foreach (string a in new[] { "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy" }) startInfo.ArgumentList.Add(a);
    using Process process = Process.Start(startInfo)!;
    string listing = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return listing;
}

// Reads a few frames from a DirectShow camera the way OBS does (bottom-up RGB24 DIBs, flipped by FFmpeg).
static async Task<DecodedVideoFrame> CaptureFromCamera(string ffmpeg, string device)
{
    const int Width = 1280, Height = 720, Frames = 10;
    var startInfo = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string a in new[]
    {
        "-hide_banner", "-nostdin", "-loglevel", "error", "-f", "dshow", "-video_size", $"{Width}x{Height}", "-i", $"video={device}",
        "-frames:v", $"{Frames}", "-f", "rawvideo", "-pix_fmt", "bgr24", "pipe:1"
    }) startInfo.ArgumentList.Add(a);
    using Process process = Process.Start(startInfo)!;
    Task<string> errors = process.StandardError.ReadToEndAsync();
    using var output = new MemoryStream();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    try { await process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token); }
    catch (OperationCanceledException) { process.Kill(); }
    await process.WaitForExitAsync();
    int frameSize = Width * Height * 3;
    if (output.Length < frameSize)
        throw new InvalidOperationException($"captured {output.Length} bytes from {device}; ffmpeg: {(await errors).Trim()}");
    byte[] last = output.ToArray()[(int)((output.Length / frameSize - 1) * frameSize)..][..frameSize];
    return new DecodedVideoFrame(last, Width, Height);
}

static bool IsBlue((byte B, byte G, byte R) p) => p.B > 150 && p.R < 100 && p.G < 100;
static bool IsGreen((byte B, byte G, byte R) p) => p.G > 150 && p.R < 100 && p.B < 100;
static bool IsRed((byte B, byte G, byte R) p) => p.R > 150 && p.B < 100 && p.G < 100;
