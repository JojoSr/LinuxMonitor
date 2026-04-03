using System.Text.Json.Serialization;

namespace FortGuard.LinuxSystemMetrics.Api.Metrics;

public sealed class MetricsRootDto
{
    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = "";

    [JsonPropertyName("host")]
    public HostDto Host { get; set; } = new();

    [JsonPropertyName("cpu")]
    public CpuDto Cpu { get; set; } = new();

    [JsonPropertyName("memory")]
    public MemoryDto Memory { get; set; } = new();

    [JsonPropertyName("disk")]
    public DiskDto Disk { get; set; } = new();

    [JsonPropertyName("network")]
    public NetworkDto? Network { get; set; }

    [JsonPropertyName("processes")]
    public ProcessesDto Processes { get; set; } = new();

    [JsonPropertyName("users")]
    public List<UserDto> Users { get; set; } = [];

    [JsonPropertyName("sensors")]
    public SensorsDto Sensors { get; set; } = new();
}

public sealed class HostDto
{
    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = "";

    [JsonPropertyName("system")]
    public string System { get; set; } = "Linux";

    [JsonPropertyName("release")]
    public string Release { get; set; } = "";

    [JsonPropertyName("machine")]
    public string Machine { get; set; } = "";

    /// <summary>Parity with Python add-on key; not applicable to .NET — omitted when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("python")]
    public string? Python { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("dotnet")]
    public string? Dotnet { get; set; }

    [JsonPropertyName("boot_time_unix")]
    public double BootTimeUnix { get; set; }

    [JsonPropertyName("uptime_seconds")]
    public int UptimeSeconds { get; set; }
}

public sealed class CpuDto
{
    [JsonPropertyName("percent_total")]
    public double PercentTotal { get; set; }

    [JsonPropertyName("percent_per_cpu")]
    public List<double> PercentPerCpu { get; set; } = [];

    [JsonPropertyName("logical_count")]
    public int LogicalCount { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("physical_count")]
    public int? PhysicalCount { get; set; }

    [JsonPropertyName("frequency_mhz")]
    public double? FrequencyMhz { get; set; }

    [JsonPropertyName("times")]
    public Dictionary<string, double> Times { get; set; } = new();

    [JsonPropertyName("load_average")]
    public List<double>? LoadAverage { get; set; }
}

public sealed class MemoryDto
{
    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("available_bytes")]
    public long AvailableBytes { get; set; }

    [JsonPropertyName("used_bytes")]
    public long UsedBytes { get; set; }

    [JsonPropertyName("percent")]
    public double Percent { get; set; }

    [JsonPropertyName("swap_total_bytes")]
    public long SwapTotalBytes { get; set; }

    [JsonPropertyName("swap_used_bytes")]
    public long SwapUsedBytes { get; set; }

    [JsonPropertyName("swap_percent")]
    public double SwapPercent { get; set; }
}

public sealed class DiskDto
{
    [JsonPropertyName("partitions")]
    public List<PartitionDto> Partitions { get; set; } = [];
}

public sealed class PartitionDto
{
    [JsonPropertyName("device")]
    public string Device { get; set; } = "";

    [JsonPropertyName("mountpoint")]
    public string Mountpoint { get; set; } = "";

    [JsonPropertyName("fstype")]
    public string Fstype { get; set; } = "";

    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("used_bytes")]
    public long UsedBytes { get; set; }

    [JsonPropertyName("free_bytes")]
    public long FreeBytes { get; set; }

    [JsonPropertyName("percent_used")]
    public double PercentUsed { get; set; }
}

public sealed class NetworkDto
{
    [JsonPropertyName("bytes_sent")]
    public long BytesSent { get; set; }

    [JsonPropertyName("bytes_recv")]
    public long BytesRecv { get; set; }

    [JsonPropertyName("packets_sent")]
    public long PacketsSent { get; set; }

    [JsonPropertyName("packets_recv")]
    public long PacketsRecv { get; set; }

    [JsonPropertyName("errin")]
    public long Errin { get; set; }

    [JsonPropertyName("errout")]
    public long Errout { get; set; }

    [JsonPropertyName("dropin")]
    public long Dropin { get; set; }

    [JsonPropertyName("dropout")]
    public long Dropout { get; set; }
}

public sealed class ProcessesDto
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("top_by_cpu")]
    public List<ProcessRowDto> TopByCpu { get; set; } = [];
}

public sealed class ProcessRowDto
{
    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("cpu_percent")]
    public double CpuPercent { get; set; }

    [JsonPropertyName("memory_percent")]
    public double MemoryPercent { get; set; }

    [JsonPropertyName("rss_bytes")]
    public long? RssBytes { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
}

public sealed class UserDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("terminal")]
    public string Terminal { get; set; } = "";

    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("started")]
    public double Started { get; set; }
}

public sealed class SensorsDto
{
    [JsonPropertyName("temperatures")]
    public Dictionary<string, List<TempEntryDto>>? Temperatures { get; set; }

    [JsonPropertyName("fans")]
    public Dictionary<string, List<FanEntryDto>>? Fans { get; set; }
}

public sealed class TempEntryDto
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("current_c")]
    public double? CurrentC { get; set; }

    [JsonPropertyName("high")]
    public double? High { get; set; }

    [JsonPropertyName("critical")]
    public double? Critical { get; set; }
}

public sealed class FanEntryDto
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("rpm")]
    public double? Rpm { get; set; }
}
