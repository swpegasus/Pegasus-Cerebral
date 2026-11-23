using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Cerebral.Auditing.Laya;

/// <summary>Frozen ordinary-user runtime configuration. Changes require explicit package readmission.</summary>
public sealed record LayaConfiguration
{
    public const string FileName = "laya.ini";
    public const string PackageVersion = "0.3.20";
    public const string CheckpointRevision = "1a793eb568e6718f15941d08f85432581df534e3";
    public const string WheelSha256 = "6039e802fa5effb8dd492061cd7ad39a43087beadc4a4fa4a649614e77eb83d4";
    public const string WeightsSha256 = "4fa56de72383a9d3efa9cfa78955733c81b9fc8067a587ca4beb82c78107a24e";
    public bool Enabled { get; init; }
    public string ConfigurationPath { get; init; } = "";
    public string PythonPath { get; init; } = "";
    public string PythonSha256 { get; init; } = "";
    public string RunnerPath { get; init; } = "";
    public string RunnerSha256 { get; init; } = "";
    public string RendererPath { get; init; } = "";
    public string RendererSha256 { get; init; } = "";
    public string DependencyLockPath { get; init; } = "";
    public string DependencyLockSha256 { get; init; } = "";
    public string RootDirectory { get; init; } = "";
    public string OriginalInputsSha256 { get; init; } = "";
    public string EffectiveInputsSha256 { get; init; } = "";
    public int TorchThreads { get; init; } = 2;
    public int InteropThreads { get; init; } = 1;
    public int QueueCapacity { get; init; } = 4;
    public int StartupTimeoutMilliseconds { get; init; } = 20_000;
    public int RequestTimeoutMilliseconds { get; init; } = 60_000;
    public int StopTimeoutMilliseconds { get; init; } = 10_000;
    /// <summary>Owner-supplied latency estimate; this is not claimed to be a measured percentile.</summary>
    public int EstimatedP95Milliseconds { get; init; }
    public long Generation { get; init; }
    public IReadOnlyList<string> Purposes { get; init; } = [];

    public static LayaConfiguration Read(string path)
    {
        path = Path.GetFullPath(path);
        if (new FileInfo(path).Length > 65_536) throw new InvalidDataException("Laya INI exceeds its bounded configuration size.");
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        string? section = null;
        foreach (var raw in File.ReadAllLines(path, new UTF8Encoding(false, true)))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                if (section is not ("host" or "laya") || !sections.TryAdd(section, new(StringComparer.Ordinal)))
                    throw new InvalidDataException("Laya INI requires unique host and laya sections.");
                continue;
            }
            var split = line.IndexOf('=');
            if (section is null || split < 1 || !sections[section].TryAdd(line[..split].Trim(), line[(split + 1)..].Trim()))
                throw new InvalidDataException("Laya INI has a malformed or duplicated setting.");
        }
        if (!sections.TryGetValue("host", out var host)) throw new InvalidDataException("Laya INI has no host section.");
        CheckKeys(host, ["enabled", "python_path", "python_sha256", "runner_path", "runner_sha256", "renderer_path", "renderer_sha256", "dependency_lock_path",
            "dependency_lock_sha256", "queue_capacity", "startup_timeout_ms", "request_timeout_ms", "stop_timeout_ms",
            "estimated_p95_ms", "generation", "purposes"]);
        if (!bool.TryParse(Value(host, "enabled"), out var enabled)) throw new InvalidDataException("Laya enabled must be true or false.");
        if (!enabled) return new() { Enabled = false, ConfigurationPath = path };
        if (!sections.TryGetValue("laya", out var native)) throw new InvalidDataException("Enabled Laya requires its native section.");
        CheckKeys(native, ["root_directory", "dependency_lock_sha256", "expected_weights_sha256", "expected_original_inputs_sha256",
            "expected_effective_inputs_sha256", "checkpoint_revision", "package_version", "torch_threads", "interop_threads"]);
        if (Value(native, "package_version") != PackageVersion || Value(native, "checkpoint_revision") != CheckpointRevision ||
            HashValue(native, "expected_weights_sha256") != WeightsSha256 ||
            HashValue(native, "dependency_lock_sha256") != HashValue(host, "dependency_lock_sha256"))
            throw new InvalidDataException("Laya configuration does not bind the admitted pinned stock package and checkpoint.");
        var purposes = Value(host, "purposes").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (purposes.Length is < 1 or > 2 || purposes.Distinct(StringComparer.Ordinal).Count() != purposes.Length ||
            purposes.Any(p => p is not ("coordinator_decision_check" or "component_evidence_check")))
            throw new InvalidDataException("Laya purposes must name the explicit supported workflow checkpoints.");
        return new()
        {
            Enabled = true, ConfigurationPath = path,
            PythonPath = Absolute(host, "python_path"), PythonSha256 = HashValue(host, "python_sha256"),
            RunnerPath = Absolute(host, "runner_path"), RunnerSha256 = HashValue(host, "runner_sha256"),
            RendererPath = Absolute(host, "renderer_path"), RendererSha256 = HashValue(host, "renderer_sha256"),
            DependencyLockPath = Absolute(host, "dependency_lock_path"), DependencyLockSha256 = HashValue(host, "dependency_lock_sha256"),
            RootDirectory = Absolute(native, "root_directory"), OriginalInputsSha256 = HashValue(native, "expected_original_inputs_sha256"),
            EffectiveInputsSha256 = HashValue(native, "expected_effective_inputs_sha256"),
            TorchThreads = Number(native, "torch_threads", 1, 16, 2), InteropThreads = Number(native, "interop_threads", 1, 4, 1),
            QueueCapacity = Number(host, "queue_capacity", 0, 16, 4),
            StartupTimeoutMilliseconds = Number(host, "startup_timeout_ms", 100, 60_000, 20_000),
            RequestTimeoutMilliseconds = Number(host, "request_timeout_ms", 100, 120_000, 60_000),
            StopTimeoutMilliseconds = Number(host, "stop_timeout_ms", 100, 30_000, 10_000),
            EstimatedP95Milliseconds = Number(host, "estimated_p95_ms", 1, 120_000),
            Generation = Number(host, "generation", 1, int.MaxValue), Purposes = Array.AsReadOnly(purposes)
        };
    }

    internal void VerifyLaunchFiles()
    {
        if (!string.Equals(RendererPath, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(RunnerPath)!, "preflight.py")), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Laya renderer must be the exact admitted runner's sibling preflight.py.");
        Verify(PythonPath, PythonSha256); Verify(RunnerPath, RunnerSha256); Verify(RendererPath, RendererSha256);
        Verify(DependencyLockPath, DependencyLockSha256);
    }
    private static void Verify(string path, string expected)
    {
        using var file = File.OpenRead(path);
        if (Convert.ToHexStringLower(SHA256.HashData(file)) != expected)
            throw new InvalidDataException("A Laya launch input changed from its frozen admitted digest.");
    }
    private static string Value(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && value.Length > 0 && !value.Contains('\0') ? value
            : throw new InvalidDataException("Laya configuration is missing a required setting: " + key + ".");
    private static string Absolute(Dictionary<string, string> values, string key)
    {
        var value = Value(values, key);
        if (!Path.IsPathFullyQualified(value)) throw new InvalidDataException("Laya paths must be explicit absolute locations.");
        return Path.GetFullPath(value);
    }
    private static string HashValue(Dictionary<string, string> values, string key)
    {
        var value = Value(values, key);
        if (!LayaProtocol.IsHash(value)) throw new InvalidDataException("Laya immutable input digest is invalid.");
        return value;
    }
    private static int Number(Dictionary<string, string> values, string key, int minimum, int maximum, int? fallback = null)
    {
        if (!values.ContainsKey(key) && fallback is { } defaultValue) return defaultValue;
        if (!int.TryParse(Value(values, key), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < minimum || value > maximum)
            throw new InvalidDataException("Laya numeric setting is outside its admitted bound: " + key + ".");
        return value;
    }
    private static void CheckKeys(Dictionary<string, string> values, IReadOnlyList<string> allowed)
    {
        if (values.Keys.Except(allowed, StringComparer.Ordinal).Any()) throw new InvalidDataException("Laya INI has unsupported settings.");
    }
}
