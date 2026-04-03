using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
namespace FortGuard.LinuxSystemMetrics.Api.Metrics;

public sealed class LinuxMetricsCollector(IConfiguration configuration, ILogger<LinuxMetricsCollector> logger)
{
    private int _clkTck = 100;
    private bool _clkTckResolved;

    public async Task<MetricsRootDto> CollectAsync(CancellationToken cancellationToken = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            throw new PlatformNotSupportedException("This collector only runs on Linux.");

        var sampleSec = Math.Clamp(
            configuration.GetValue("Metrics:CpuSampleSeconds", 0.25d),
            0.05,
            5.0);
        var maxProcs = Math.Clamp(configuration.GetValue("Metrics:MaxProcesses", 40), 1, 200);

        var now = DateTimeOffset.UtcNow;
        var timestamp = now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        var hz = GetClkTck();

        var stat1 = ReadProcStat();
        var net1 = ReadProcNetDev();
        var pids = Directory.GetDirectories("/proc")
            .Select(Path.GetFileName)
            .Where(s => !string.IsNullOrEmpty(s) && s.All(char.IsDigit))
            .Select(s => int.Parse(s!, CultureInfo.InvariantCulture))
            .ToList();

        var procSample1 = new Dictionary<int, (long utime, long stime, string comm, char state)>();
        foreach (var pid in pids)
        {
            if (TryReadProcPidStat(pid, out var ut, out var st, out var comm, out var state))
                procSample1[pid] = (ut, st, comm, state);
        }

        await Task.Delay(TimeSpan.FromSeconds(sampleSec), cancellationToken).ConfigureAwait(false);

        var stat2 = ReadProcStat();
        var procSample2 = new Dictionary<int, (long utime, long stime)>();
        foreach (var pid in procSample1.Keys)
        {
            if (TryReadProcPidStat(pid, out var ut, out var st, out _, out _))
                procSample2[pid] = (ut, st);
        }

        var deltaTotal = stat2.TotalJiffies - stat1.TotalJiffies;
        var deltaIdle = stat2.IdleJiffies - stat1.IdleJiffies;
        var pctTotal = deltaTotal > 0
            ? Round2(100.0 * (deltaTotal - deltaIdle) / deltaTotal)
            : 0;

        var perCpu = new List<double>();
        var n = Math.Min(stat1.PerCpuTotals.Count, stat2.PerCpuTotals.Count);
        for (var i = 0; i < n; i++)
        {
            var dt = Math.Max(1L, stat2.PerCpuTotals[i] - stat1.PerCpuTotals[i]);
            var di = stat2.PerCpuIdles[i] - stat1.PerCpuIdles[i];
            var u = dt - di;
            perCpu.Add(Round2(100.0 * u / dt));
        }

        var mem = ReadMemInfo();
        var load = ReadLoadAverage();
        var boot = ReadBootTimeUnix();
        var uptimeSec = boot > 0 ? (int)Math.Max(0, now.ToUnixTimeSeconds() - boot) : 0;

        var partitions = CollectDiskPartitions();
        var netAgg = net1;

        var totalMem = mem.TotalBytes;
        var memPercent = totalMem > 0 ? Round2(100.0 * mem.UsedBytes / (double)totalMem) : 0;

        var procRows = new List<ProcessRowDto>();
        foreach (var (pid, s1) in procSample1)
        {
            if (!procSample2.TryGetValue(pid, out var s2))
                continue;
            var dj = (s2.utime - s1.utime) + (s2.stime - s1.stime);
            var intervalTicks = sampleSec * hz;
            var cpuPct = intervalTicks > 0
                ? 100.0 * dj / (intervalTicks * stat2.LogicalCpus)
                : 0;
            if (cpuPct < 0 || double.IsNaN(cpuPct) || double.IsInfinity(cpuPct))
                cpuPct = 0;

            long? rss = TryReadProcPidRss(pid);
            var memPct = totalMem > 0 && rss.HasValue
                ? Round2(100.0 * rss.Value / (double)totalMem)
                : 0;

            procRows.Add(new ProcessRowDto
            {
                Pid = pid,
                Name = s1.comm,
                CpuPercent = Round2(cpuPct),
                MemoryPercent = memPct,
                RssBytes = rss,
                Status = s1.state.ToString(),
            });
        }

        procRows.Sort((a, b) => b.CpuPercent.CompareTo(a.CpuPercent));
        procRows = procRows.Take(maxProcs).ToList();

        var (temps, fans) = ReadThermalAndFans();

        return new MetricsRootDto
        {
            Timestamp = timestamp,
            Host = new HostDto
            {
                Hostname = Environment.MachineName,
                System = "Linux",
                Release = ReadOsReleasePretty() ?? ReadUnameRelease() ?? "",
                Machine = RuntimeInformation.ProcessArchitecture.ToString(),
                Python = null,
                Dotnet = Environment.Version.ToString(),
                BootTimeUnix = boot,
                UptimeSeconds = uptimeSec,
            },
            Cpu = new CpuDto
            {
                PercentTotal = pctTotal,
                PercentPerCpu = perCpu,
                LogicalCount = stat2.LogicalCpus,
                PhysicalCount = null,
                FrequencyMhz = TryReadCpuMhzAvg(),
                Times = BuildCpuTimes(stat2.AggregateFields),
                LoadAverage = load,
            },
            Memory = new MemoryDto
            {
                TotalBytes = mem.TotalBytes,
                AvailableBytes = mem.AvailableBytes,
                UsedBytes = mem.UsedBytes,
                Percent = memPercent,
                SwapTotalBytes = mem.SwapTotalBytes,
                SwapUsedBytes = mem.SwapUsedBytes,
                SwapPercent = mem.SwapPercent,
            },
            Disk = new DiskDto { Partitions = partitions },
            Network = netAgg,
            Processes = new ProcessesDto
            {
                Count = pids.Count,
                TopByCpu = procRows,
            },
            Users = ReadLoggedInUsers(),
            Sensors = new SensorsDto
            {
                Temperatures = temps is { Count: > 0 } ? temps : null,
                Fans = fans is { Count: > 0 } ? fans : null,
            },
        };
    }

    private int GetClkTck()
    {
        if (_clkTckResolved)
            return _clkTck;
        _clkTckResolved = true;
        try
        {
            using var p = Process.Start(
                new ProcessStartInfo("getconf", "CLK_TCK")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                });
            if (p is null)
                return _clkTck;
            var o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            if (int.TryParse(o, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) && h > 0)
                _clkTck = h;
        }
        catch
        {
            // keep default 100
        }
        return _clkTck;
    }

    private static double ReadBootTimeUnix()
    {
        try
        {
            var line = File.ReadAllText("/proc/uptime").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (line.Length == 0)
                return 0;
            if (!double.TryParse(line[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var up))
                return 0;
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)up;
        }
        catch
        {
            return 0;
        }
    }

    private static string? ReadOsReleasePretty()
    {
        try
        {
            foreach (var line in File.ReadLines("/etc/os-release"))
            {
                if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                {
                    var v = line["PRETTY_NAME=".Length..].Trim().Trim('"');
                    return v;
                }
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    private static string? ReadUnameRelease()
    {
        try
        {
            using var p = Process.Start(
                new ProcessStartInfo("uname", "-r")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                });
            if (p is null)
                return null;
            return p.StandardOutput.ReadToEnd().Trim();
        }
        catch
        {
            return null;
        }
    }

    private static List<double>? ReadLoadAverage()
    {
        try
        {
            var parts = File.ReadAllText("/proc/loadavg").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                return null;
            var list = new List<double>();
            for (var i = 0; i < 3; i++)
            {
                if (double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    list.Add(Math.Round(v, 2));
            }
            return list.Count == 3 ? list : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed record MemParse(
        long TotalBytes,
        long AvailableBytes,
        long UsedBytes,
        long SwapTotalBytes,
        long SwapUsedBytes,
        double SwapPercent);

    private static MemParse ReadMemInfo()
    {
        long memTotal = 0, memAvail = 0, memFree = 0, buffers = 0, cached = 0, sReclaimable = 0;
        long swapTotal = 0, swapFree = 0;
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                var idx = line.IndexOf(':');
                if (idx < 0)
                    continue;
                var key = line[..idx].Trim();
                var rest = line[(idx + 1)..].Trim();
                var kbStr = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!long.TryParse(kbStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
                    continue;
                var bytes = kb * 1024;
                switch (key)
                {
                    case "MemTotal": memTotal = bytes; break;
                    case "MemAvailable": memAvail = bytes; break;
                    case "MemFree": memFree = bytes; break;
                    case "Buffers": buffers = bytes; break;
                    case "Cached": cached = bytes; break;
                    case "SReclaimable": sReclaimable = bytes; break;
                    case "SwapTotal": swapTotal = bytes; break;
                    case "SwapFree": swapFree = bytes; break;
                }
            }
        }
        catch
        {
            // leave zeros
        }

        long avail;
        if (memAvail > 0)
            avail = memAvail;
        else
            avail = memFree + buffers + cached + sReclaimable;

        var used = Math.Max(0, memTotal - avail);
        var swapUsed = Math.Max(0, swapTotal - swapFree);
        var swapPct = swapTotal > 0 ? Round2(100.0 * swapUsed / swapTotal) : 0;
        return new MemParse(memTotal, avail, used, swapTotal, swapUsed, swapPct);
    }

    private sealed record CpuStatSnapshot(
        long TotalJiffies,
        long IdleJiffies,
        int LogicalCpus,
        List<long> PerCpuTotals,
        List<long> PerCpuIdles,
        long[] AggregateFields);

    private static CpuStatSnapshot ReadProcStat()
    {
        var perTotals = new List<long>();
        var perIdles = new List<long>();
        long aggTotal = 0, aggIdle = 0;
        long[] aggFields = [];
        try
        {
            using var sr = new StreamReader("/proc/stat");
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (line.StartsWith("cpu ", StringComparison.Ordinal))
                {
                    var nums = ParseLongTail(line);
                    aggFields = nums;
                    aggTotal = nums.Sum();
                    aggIdle = nums.Length > 4 ? nums[3] + nums[4] : nums[3];
                }
                else if (line.StartsWith("cpu", StringComparison.Ordinal) && line.Length > 3 && char.IsDigit(line[3]))
                {
                    var nums = ParseLongTail(line);
                    perTotals.Add(nums.Sum());
                    perIdles.Add(nums.Length > 4 ? nums[3] + nums[4] : nums[3]);
                }
            }
        }
        catch
        {
            // empty
        }

        return new CpuStatSnapshot(aggTotal, aggIdle, perTotals.Count > 0 ? perTotals.Count : Environment.ProcessorCount, perTotals, perIdles, aggFields);
    }

    private static long[] ParseLongTail(string line)
    {
        var sp = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var list = new List<long>();
        for (var i = 1; i < sp.Length; i++)
        {
            if (long.TryParse(sp[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                list.Add(v);
        }
        return list.ToArray();
    }

    private static Dictionary<string, double> BuildCpuTimes(long[] fields)
    {
        // Labels aligned with common /proc/stat cpu fields
        var keys = new[] { "user", "nice", "system", "idle", "iowait", "irq", "softirq", "steal", "guest", "guest_nice" };
        var d = new Dictionary<string, double>();
        for (var i = 0; i < fields.Length && i < keys.Length; i++)
            d[keys[i]] = fields[i];
        return d;
    }

    private static NetworkDto? ReadProcNetDev()
    {
        long br = 0, bt = 0, pr = 0, pt = 0, ein = 0, eout = 0, din = 0, dout = 0;
        try
        {
            var lines = File.ReadAllLines("/proc/net/dev");
            for (var i = 2; i < lines.Length; i++)
            {
                var line = lines[i];
                var idx = line.IndexOf(':');
                if (idx < 0)
                    continue;
                var iface = line[..idx].Trim();
                if (iface == "lo")
                    continue;
                var nums = line[(idx + 1)..]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0)
                    .ToArray();
                if (nums.Length < 16)
                    continue;
                br += nums[0];
                pr += nums[1];
                ein += nums[2];
                din += nums[3];
                bt += nums[8];
                pt += nums[9];
                eout += nums[10];
                dout += nums[11];
            }
        }
        catch
        {
            return null;
        }

        return new NetworkDto
        {
            BytesRecv = br,
            PacketsRecv = pr,
            Errin = ein,
            Dropin = din,
            BytesSent = bt,
            PacketsSent = pt,
            Errout = eout,
            Dropout = dout,
        };
    }

    private static List<PartitionDto> CollectDiskPartitions()
    {
        var list = new List<PartitionDto>();
        try
        {
            foreach (var di in DriveInfo.GetDrives())
            {
                if (di.DriveType != DriveType.Fixed && di.DriveType != DriveType.Removable && di.DriveType != DriveType.Network)
                    continue;
                if (!di.IsReady)
                    continue;
                try
                {
                    var total = di.TotalSize;
                    if (total <= 0)
                        continue;
                    var free = di.AvailableFreeSpace;
                    var used = total - free;
                    var pct = 100.0 * used / total;
                    list.Add(new PartitionDto
                    {
                        Device = di.Name.TrimEnd('/'),
                        Mountpoint = di.RootDirectory.FullName.TrimEnd('/') + "/",
                        Fstype = di.DriveFormat,
                        TotalBytes = total,
                        UsedBytes = used,
                        FreeBytes = free,
                        PercentUsed = Round2(pct),
                    });
                }
                catch
                {
                    // skip mount
                }
            }
        }
        catch
        {
            // ignore
        }

        list.Sort((a, b) => string.Compare(a.Mountpoint, b.Mountpoint, StringComparison.Ordinal));
        return list;
    }

    private static bool TryReadProcPidStat(
        int pid,
        out long utime,
        out long stime,
        out string comm,
        out char state)
    {
        utime = stime = 0;
        comm = "?";
        state = '?';
        try
        {
            var line = File.ReadAllText($"/proc/{pid}/stat");
            var rp = line.LastIndexOf(')');
            if (rp < 0)
                return false;
            var lp = line.IndexOf('(');
            if (lp < 0 || rp <= lp)
                return false;
            comm = line[(lp + 1)..rp];
            var tail = line[(rp + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tail.Length < 15)
                return false;
            state = tail[0][0];
            if (!long.TryParse(tail[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out utime))
                return false;
            if (!long.TryParse(tail[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out stime))
                return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static long? TryReadProcPidRss(int pid)
    {
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/status"))
            {
                if (!line.StartsWith("VmRSS:", StringComparison.Ordinal))
                    continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    return null;
                if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
                    return null;
                return kb * 1024;
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    private static double? TryReadCpuMhzAvg()
    {
        var values = new List<double>();
        try
        {
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                if (!line.StartsWith("cpu MHz", StringComparison.OrdinalIgnoreCase))
                    continue;
                var idx = line.IndexOf(':');
                if (idx < 0)
                    continue;
                var tail = line[(idx + 1)..].Trim();
                if (double.TryParse(tail, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz))
                    values.Add(mhz);
            }
        }
        catch
        {
            return null;
        }
        return values.Count > 0 ? Math.Round(values.Average(), 2) : null;
    }

    private (Dictionary<string, List<TempEntryDto>>? temps, Dictionary<string, List<FanEntryDto>>? fans) ReadThermalAndFans()
    {
        var temps = new Dictionary<string, List<TempEntryDto>>();
        var fans = new Dictionary<string, List<FanEntryDto>>();
        try
        {
            var thermalRoot = "/sys/class/thermal";
            if (Directory.Exists(thermalRoot))
            {
                foreach (var zoneDir in Directory.GetDirectories(thermalRoot, "thermal_zone*"))
                {
                    var typePath = Path.Combine(zoneDir, "type");
                    var tempPath = Path.Combine(zoneDir, "temp");
                    if (!File.Exists(tempPath))
                        continue;
                    var type = File.Exists(typePath) ? File.ReadAllText(typePath).Trim() : Path.GetFileName(zoneDir);
                    if (!long.TryParse(File.ReadAllText(tempPath).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var milli))
                        continue;
                    var c = milli / 1000.0;
                    if (!temps.TryGetValue("thermal", out var list))
                    {
                        list = [];
                        temps["thermal"] = list;
                    }
                    list.Add(new TempEntryDto { Label = type, CurrentC = Math.Round(c, 2), High = null, Critical = null });
                }
            }

            var hwmonRoot = "/sys/class/hwmon";
            if (Directory.Exists(hwmonRoot))
            {
                foreach (var hm in Directory.GetDirectories(hwmonRoot))
                {
                    var name = Path.GetFileName(hm);
                    foreach (var f in Directory.GetFiles(hm, "temp*_input"))
                    {
                        if (!TryReadSysLongFile(f, out var micro))
                            continue;
                        var label = ReadHwmonLabel(hm, Path.GetFileName(f).Replace("_input", "_label", StringComparison.Ordinal));
                        var c = micro / 1000.0;
                        if (!temps.TryGetValue(name, out var tlist))
                        {
                            tlist = [];
                            temps[name] = tlist;
                        }
                        tlist.Add(new TempEntryDto { Label = label ?? Path.GetFileName(f), CurrentC = Math.Round(c, 2) });
                    }

                    foreach (var f in Directory.GetFiles(hm, "fan*_input"))
                    {
                        if (!TryReadSysLongFile(f, out var rpm))
                            continue;
                        var label = ReadHwmonLabel(hm, Path.GetFileName(f).Replace("_input", "_label", StringComparison.Ordinal));
                        if (!fans.TryGetValue(name, out var flist))
                        {
                            flist = [];
                            fans[name] = flist;
                        }
                        flist.Add(new FanEntryDto { Label = label ?? Path.GetFileName(f), Rpm = rpm });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Thermal/fan enumeration failed");
        }

        return (temps, fans);
    }

    private static string? ReadHwmonLabel(string hmDir, string labelFile)
    {
        try
        {
            var p = Path.Combine(hmDir, labelFile);
            return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryReadSysLongFile(string path, out long value)
    {
        value = 0;
        try
        {
            var s = File.ReadAllText(path).Trim();
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
        catch
        {
            return false;
        }
    }

    private static List<UserDto> ReadLoggedInUsers()
    {
        var list = new List<UserDto>();
        try
        {
            using var p = Process.Start(
                new ProcessStartInfo("who")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                });
            if (p is null)
                return list;
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    continue;
                list.Add(new UserDto
                {
                    Name = parts[0],
                    Terminal = parts[1],
                    Host = parts.Length > 2 ? parts[2].TrimStart('(').TrimEnd(')') : "",
                    Started = 0,
                });
            }
        }
        catch
        {
            // ignore
        }
        return list;
    }

    private static double Round2(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
