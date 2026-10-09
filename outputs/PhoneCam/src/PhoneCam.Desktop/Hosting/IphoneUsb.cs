using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace PhoneCam.Desktop.Hosting;

/// <summary>What Windows currently sees of an iPhone on the USB cable.</summary>
/// <param name="AppleSoftware">The usbmux function (MI_01) exists, i.e. Apple Devices/iTunes runs: without it Apple's filter keeps the phone in camera-only mode.</param>
/// <param name="TetherFunction">The phone exposes its USB Ethernet function (MI_02), which it asks for only while Personal Hotspot is on.</param>
/// <param name="CarriesInternet">Windows routes the PC's internet traffic through the phone (cellular data) although another uplink exists.</param>
public sealed record IphoneUsbStatus(bool PhoneConnected, bool AppleSoftware, bool TetherFunction, bool AdapterPresent, IPAddress? Address, int? InterfaceIndex, bool CarriesInternet);

/// <summary>
/// iPhone over USB = Personal Hotspot over the cable. Apple's driver (installed with the Apple Devices app or
/// iTunes) turns the phone into an Ethernet adapter in 172.20.10.0/28, and Safari reaches the PC through it
/// exactly as over Wi-Fi, so the phone page, WHIP and MediaMTX need no changes. The Ethernet function (MI_02)
/// exists only in a USB configuration the phone requests while Personal Hotspot is on; Apple's lower filter
/// polls that preference and re-enumerates the phone. The Apple Devices app does not ship the matching
/// driver (netaapl64, "Apple Mobile Device Ethernet"); it comes from Windows Update / the Update Catalog.
/// </summary>
internal static class IphoneUsb
{
    public const string AdapterDescription = "Apple Mobile Device Ethernet";
    public const string HotspotSubnet = "172.20.10.0/28";
    public const string AppleDevicesStoreUri = "ms-windows-store://pdp/?ProductId=9NP83LWLPZ9K";

    // Apple vendor id; product ids 0x12xx are iPhone/iPad/iPod in normal (not recovery) mode.
    private const string AppleMobileDevicePrefix = @"USB\VID_05AC&PID_12";
    // High enough to lose against any Wi-Fi/Ethernet uplink, so the phone carries only PhoneCam's subnet.
    private const int QuietMetric = 4000;
    private static readonly IPAddress InternetProbe = IPAddress.Parse("1.1.1.1");

    public static bool IsAdapter(NetworkInterface nic) =>
        nic.Description.Contains(AdapterDescription, StringComparison.OrdinalIgnoreCase);

    public static IphoneUsbStatus Probe()
    {
        NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
        NetworkInterface? adapter = interfaces.Where(IsAdapter).OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up).FirstOrDefault();
        string[] phone = PresentDeviceIds("USB").Where(id => id.StartsWith(AppleMobileDevicePrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        bool HasFunction(string mi) => phone.Any(id => id.Contains(mi, StringComparison.OrdinalIgnoreCase));

        IPAddress? address = null;
        int? index = null;
        if (adapter is { OperationalStatus: OperationalStatus.Up })
        {
            IPInterfaceProperties properties = adapter.GetIPProperties();
            address = properties.UnicastAddresses.Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && a.GetAddressBytes() is not [169, 254, ..]);
            index = TryIPv4Index(properties);
        }

        bool otherUplink = interfaces.Any(n => !IsAdapter(n) && n.OperationalStatus == OperationalStatus.Up
            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            && n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)));
        bool carriesInternet = index is not null && otherUplink && BestInterfaceIndex(InternetProbe) == index;

        return new IphoneUsbStatus(phone.Length > 0 || adapter is not null, HasFunction("&MI_01"), HasFunction("&MI_02"), adapter is not null, address, index, carriesInternet);
    }

    /// <summary>Raises the phone adapter's metric so the PC's internet stays on Wi-Fi/Ethernet. Returns false if UAC was declined.</summary>
    public static Task<bool> StopCarryingInternetAsync(int interfaceIndex) =>
        Elevated.RunScriptAsync("usb-metric.cmd",
            $"netsh interface ipv4 set interface interface={interfaceIndex} metric={QuietMetric} store=persistent\r\n" +
            $"netsh interface ipv6 set interface interface={interfaceIndex} metric={QuietMetric} store=persistent >nul 2>&1\r\n");

    /// <summary>Instance ids of devices currently present under a PnP enumerator (e.g. "USB").</summary>
    internal static IReadOnlyList<string> PresentDeviceIds(string enumerator)
    {
        const uint filter = CmGetIdListFilterEnumerator | CmGetIdListFilterPresent;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out uint length, enumerator, filter) != CrSuccess) return [];
            var buffer = new char[length];
            int result = CM_Get_Device_ID_ListW(enumerator, buffer, length, filter);
            if (result == CrBufferSmall) continue; // a device arrived between the two calls
            if (result != CrSuccess) return [];
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        return [];
    }

    /// <summary>Index of the interface Windows would use to reach <paramref name="destination"/>.</summary>
    internal static int? BestInterfaceIndex(IPAddress destination)
    {
        uint address = BitConverter.ToUInt32(destination.GetAddressBytes()); // IPAddr: network byte order in memory
        return GetBestInterface(address, out uint index) == 0 ? (int)index : null;
    }

    private static int? TryIPv4Index(IPInterfaceProperties properties)
    {
        try { return properties.GetIPv4Properties()?.Index; }
        catch (NetworkInformationException) { return null; }
    }

    private const int CrSuccess = 0;
    private const int CrBufferSmall = 0x1A;
    private const uint CmGetIdListFilterEnumerator = 0x1;
    private const uint CmGetIdListFilterPresent = 0x100;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint length, uint flags);

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destination, out uint bestInterfaceIndex);
}
