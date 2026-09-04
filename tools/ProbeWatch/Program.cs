using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using StreamTweak;

// --- ProbeWatch --------------------------------------------------------------
// Measures one thing: does LogParser.HasActiveMoonlightSession() keep reporting "live" for the
// whole length of a real streaming session?
//
// Why it exists. The probe is already the authority when StreamTweak starts (it decides whether
// a session found in the log is still running) and the hard guard against renegotiating the
// adapter under a live stream. The one place it is never consulted is *during* a session — and
// that is exactly where a watchdog for clients that send no telemetry would have to consult it
// (issue #9: with a stock Moonlight client nothing at all closes a session that the server never
// logged the end of). Before that watchdog can be written, the probe has to be shown to be
// steady, because a probe that dips to "no" mid-session would end someone's game.
//
// It records the verdict AND the raw evidence behind it — every ESTABLISHED socket on port 48010
// — so a dip can be read rather than guessed at.
//
// Usage:   ProbeWatch.exe [seconds between polls, default 10] [minutes to run, default until Ctrl+C]
//
// ⚠️ Each poll adds one line to %LOCALAPPDATA%\StreamTweak\debug.log, because the probe being
//    measured is the production one and it logs its own outcome. That is one line per poll in the
//    same file StreamTweak writes; nothing is deleted, and the run is not restored afterwards
//    (unlike LinkSim, which does roll the log back — doing that here would throw away the session's
//    real log entries, which are the point of comparison).

const int RtspPort = 48010;

int intervalSec = args.Length > 0 && int.TryParse(args[0], out int a) && a > 0 ? a : 10;
int limitMin    = args.Length > 1 && int.TryParse(args[1], out int b) && b > 0 ? b : 0;
int infoPort    = args.Length > 2 && int.TryParse(args[2], out int c) && c > 0 ? c : 47989;

// The third candidate: the UDP sockets the session itself runs on. Measured idle on 04/09/2026,
// the server process holds four TCP listeners (47984/47989/47990/48010) and NOT ONE UDP socket —
// so a UDP socket appearing is a session starting. What this run is for is the other half: whether
// they are released when the client goes away, including with the game left running, which is
// where <state> failed.

// The candidate replacement signal. The server answers this unauthenticated over plain HTTP on
// loopback, and <state> is the field a Moonlight client itself reads to know whether the host is
// busy. Verified reachable 04/09/2026 (SUNSHINE_SERVER_FREE with no session); what this run is
// for is seeing what it says while a session is up.
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

async Task<(string State, string Game)> ReadServerInfoAsync()
{
    try
    {
        string xml = await http.GetStringAsync($"http://127.0.0.1:{infoPort}/serverinfo");
        string Field(string tag)
        {
            int i = xml.IndexOf($"<{tag}>", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "-";
            i += tag.Length + 2;
            int j = xml.IndexOf($"</{tag}>", i, StringComparison.OrdinalIgnoreCase);
            return j > i ? xml.Substring(i, j - i) : "-";
        }
        // SUNSHINE_SERVER_FREE / SUNSHINE_SERVER_BUSY — trimmed to the last word for the table.
        string state = Field("state");
        int us = state.LastIndexOf('_');
        return (us >= 0 ? state.Substring(us + 1) : state, Field("currentgame"));
    }
    catch (Exception ex)
    {
        return ("ERR:" + ex.GetType().Name, "-");
    }
}

string csvPath = Path.Combine(
    Directory.GetCurrentDirectory(),
    $"probewatch-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

var info = LogParser.FindStreamingAppInfo();
string serverName = info?.ExePath is { } exe ? Path.GetFileNameWithoutExtension(exe) : "(not found)";
int[] pids = info?.ExePath is { } e2
    ? Process.GetProcessesByName(Path.GetFileNameWithoutExtension(e2)).Select(p => p.Id).ToArray()
    : Array.Empty<int>();

Console.WriteLine("ProbeWatch - is the RTSP socket steady for a whole session?");
Console.WriteLine($"  server          : {info?.AppName ?? "(none)"}  [{serverName}]  pid(s): "
                + (pids.Length > 0 ? string.Join(", ", pids) : "not running"));
Console.WriteLine($"  probe           : LogParser.HasActiveMoonlightSession()");
Console.WriteLine($"  every           : {intervalSec}s" + (limitMin > 0 ? $", for {limitMin} min" : ", until Ctrl+C"));
Console.WriteLine($"  csv             : {csvPath}");
Console.WriteLine();
Console.WriteLine("  Start a normal session, play for a while, then stop it and press Ctrl+C.");
Console.WriteLine("  What matters is the run BETWEEN the first and last 'live' - no gaps in it.");
Console.WriteLine();
Console.WriteLine("   time      probe    /serverinfo   game   server UDP ports      48010 established");
Console.WriteLine("   --------  -------  ------------  -----  -------------------  -----------------");

var rows = new List<(DateTime When, bool Live, int Sockets, string State, int UdpCount)>();
using var writer = new StreamWriter(csvPath) { AutoFlush = true };
writer.WriteLine("timestamp,probe_live,established_48010,serverinfo_state,currentgame,udp_ports,remote_endpoints");

bool stopping = false;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping = true; };

DateTime deadline = limitMin > 0 ? DateTime.Now.AddMinutes(limitMin) : DateTime.MaxValue;

while (!stopping && DateTime.Now < deadline)
{
    DateTime now = DateTime.Now;
    bool live = LogParser.HasActiveMoonlightSession();

    // The raw evidence, read independently of the probe: every established socket on the RTSP
    // port with a remote that is not loopback. This is what the probe is looking for, so a
    // disagreement between the two columns is itself worth seeing.
    string[] remotes;
    try
    {
        remotes = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
            .Where(c => c.LocalEndPoint.Port == RtspPort
                     && c.State == TcpState.Established
                     && !IPAddress.IsLoopback(c.RemoteEndPoint.Address))
            .Select(c => c.RemoteEndPoint.ToString())
            .ToArray();
    }
    catch (Exception ex)
    {
        remotes = new[] { "error: " + ex.Message };
    }

    var (state, game) = await ReadServerInfoAsync();

    // Re-read the pids every poll: the server may have been restarted under us.
    int[] livePids = info?.ExePath is { } e3
        ? Process.GetProcessesByName(Path.GetFileNameWithoutExtension(e3)).Select(p => p.Id).ToArray()
        : Array.Empty<int>();
    int[] udpPorts = Udp.PortsOf(livePids);
    string udpText = udpPorts.Length == 0 ? "-" : string.Join(" ", udpPorts);

    rows.Add((now, live, remotes.Length, state, udpPorts.Length));
    writer.WriteLine($"{now:yyyy-MM-dd HH:mm:ss},{(live ? "live" : "no")},{remotes.Length},"
                   + $"{state},{game},\"{udpText}\",\"{string.Join(" ", remotes)}\"");
    Console.WriteLine($"   {now:HH:mm:ss}  {(live ? "LIVE   " : "no     ")}  {state,-12}  {game,-5}  {udpText,-19}  "
                    + (remotes.Length == 0 ? "-" : string.Join("  ", remotes)));

    for (int slept = 0; slept < intervalSec * 10 && !stopping; slept++)
        Thread.Sleep(100);
}

// --- Summary -----------------------------------------------------------------
Console.WriteLine();
Console.WriteLine($"polls: {rows.Count}   probe live: {rows.Count(r => r.Live)}   probe no: {rows.Count(r => !r.Live)}");

// The second signal, summarised on its own: how many polls each distinct <state> accounted for.
// A state that tracks the session is what the watchdog would be built on instead of the probe.
foreach (var g in rows.GroupBy(r => r.State).OrderByDescending(g => g.Count()))
    Console.WriteLine($"  /serverinfo {g.Key,-14} {g.Count(),4} poll(s)   "
                    + $"{g.Min(r => r.When):HH:mm:ss} -> {g.Max(r => r.When):HH:mm:ss}");

// The third signal. Its whole value is in the boundaries, so print the polls where the count
// changed rather than a tally: that is where a disconnect or a game exit shows up.
Console.WriteLine();
Console.WriteLine("  server UDP sockets — every change:");
int prevUdp = -1;
foreach (var r in rows)
{
    if (r.UdpCount == prevUdp) continue;
    Console.WriteLine($"    {r.When:HH:mm:ss}  {prevUdp,3} -> {r.UdpCount,-3}");
    prevUdp = r.UdpCount;
}

int firstLive = rows.FindIndex(r => r.Live);
int lastLive  = rows.FindLastIndex(r => r.Live);

if (firstLive < 0)
{
    Console.WriteLine("The probe never reported a live session - either no session ran during the");
    Console.WriteLine("measurement, or it cannot see this server. Check the pid line at the top.");
}
else
{
    var inside = rows.GetRange(firstLive, lastLive - firstLive + 1);
    int dips = inside.Count(r => !r.Live);

    // Longest unbroken run of "no" between the first and the last "live" — the number a watchdog
    // would have to tolerate. Three strikes at 30 s only survives a dip shorter than 90 s.
    int worst = 0, run = 0;
    foreach (var r in inside)
    {
        run = r.Live ? 0 : run + 1;
        if (run > worst) worst = run;
    }

    Console.WriteLine($"session window: {inside[0].When:HH:mm:ss} -> {inside[^1].When:HH:mm:ss} "
                    + $"({(inside[^1].When - inside[0].When).TotalMinutes.ToString("0.0", CultureInfo.InvariantCulture)} min, "
                    + $"{inside.Count} polls)");

    if (dips == 0)
    {
        Console.WriteLine("STEADY - the probe said live at every poll inside the session window.");
        Console.WriteLine("A watchdog built on it is safe for the length measured here.");
    }
    else
    {
        Console.WriteLine($"DIPPED - {dips} poll(s) inside the window said no; longest unbroken dip: "
                        + $"{worst} poll(s) ~ {worst * intervalSec}s.");
        Console.WriteLine("A watchdog must tolerate more than that, or not be built on this probe.");
        foreach (var r in inside.Where(r => !r.Live))
            Console.WriteLine($"    dip at {r.When:HH:mm:ss}");
    }
}

Console.WriteLine();
Console.WriteLine($"csv written: {csvPath}");

static class Udp
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID { public uint dwLocalAddr, dwLocalPort, dwOwningPid; }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order,
                                                   int af, int tableClass, uint reserved);

    private const int AF_INET = 2, UDP_TABLE_OWNER_PID = 1;

    /// <summary>Local UDP ports currently bound by any of <paramref name="pids"/> (IPv4).</summary>
    public static int[] PortsOf(int[] pids)
    {
        if (pids.Length == 0) return Array.Empty<int>();

        int len = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref len, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
        IntPtr buf = Marshal.AllocHGlobal(len);
        try
        {
            if (GetExtendedUdpTable(buf, ref len, false, AF_INET, UDP_TABLE_OWNER_PID, 0) != 0)
                return Array.Empty<int>();

            int rows = Marshal.ReadInt32(buf);
            int size = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
            var ports = new List<int>();
            for (int i = 0; i < rows; i++)
            {
                var r = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(buf + 4 + i * size);
                if (!pids.Contains((int)r.dwOwningPid)) continue;
                // Network byte order, same as the TCP table.
                ports.Add((int)(((r.dwLocalPort & 0xFF) << 8) | ((r.dwLocalPort >> 8) & 0xFF)));
            }
            ports.Sort();
            return ports.ToArray();
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
