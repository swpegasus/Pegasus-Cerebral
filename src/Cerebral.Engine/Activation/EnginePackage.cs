using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Cerebral.Engine.Contracts;

namespace Cerebral.Engine.Activation;

public sealed record EnginePackageInfo(string Slot, string Id, string Version, string EntryType,
    string AssemblyName, string AssemblySha256, string ConfigurationSha256, string PackageSha256);

internal static class EngineIni
{
    public static Dictionary<string, string> Read(byte[] bytes, string section)
    {
        if (bytes.Length > 32 * 1024) throw new InvalidDataException("Engine INI exceeds 32 KiB.");
        var text = new UTF8Encoding(false, true).GetString(bytes);
        if (text.StartsWith('\uFEFF')) text = text[1..];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var selected = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line == "[" + section + "]" && !selected) { selected = true; continue; }
            var equals = line.IndexOf('=');
            if (!selected || equals <= 0 || values.Count >= 64) throw new InvalidDataException("Invalid engine INI section or key.");
            var key = line[..equals].Trim(); var value = line[(equals + 1)..].Trim();
            if (key.Length > 100 || value.Length > 8192 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
                value.Contains('\0') || !values.TryAdd(key, value)) throw new InvalidDataException("Invalid or repeated engine INI key.");
        }
        if (!selected) throw new InvalidDataException("Engine INI section is missing.");
        return values;
    }
    public static byte[] Write(string section, IReadOnlyDictionary<string, string> values) => Encoding.UTF8.GetBytes(
        "[" + section + "]\n" + string.Join("\n", values.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value)) + "\n");
    public static void AtomicWrite(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path)!;
        EnginePackage.CheckPath(directory);
        Directory.CreateDirectory(directory);
        EnginePackage.CheckPath(path);
        var temporary = Path.Combine(directory, ".engine-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough)) { file.Write(bytes); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed record EnginePackage(EnginePackageInfo Info, byte[] AssemblyBytes, EngineConfiguration Configuration)
{
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static void CheckPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Engine paths cannot traverse symbolic links or reparse points.");
    }
    internal static byte[] Read(string path, int maximum)
    {
        CheckPath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > maximum) throw new InvalidDataException("Engine file exceeds its admitted size.");
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes); return bytes;
    }
    public static EnginePackage ReadSlot(string root, string slot)
    {
        if (slot is not ("A" or "B")) throw new ArgumentException("Engine slot must be A or B.");
        var directory = Path.Combine(Path.GetFullPath(root), slot);
        var manifestBytes = Read(Path.Combine(directory, "engine.ini"), 32 * 1024);
        var ini = EngineIni.Read(manifestBytes, "engine");
        var expectedKeys = new[] { "id", "version", "protocol", "assembly", "assembly-sha256", "entry-type", "configuration-sha256" };
        if (!ini.Keys.Order().SequenceEqual(expectedKeys.Order()) || ini["protocol"] != "pegasus.cerebral.engine/1")
            throw new InvalidDataException("Unsupported or incomplete engine manifest.");
        var assemblyName = ini["assembly"];
        if (Path.GetFileName(assemblyName) != assemblyName || !assemblyName.EndsWith(".dll", StringComparison.Ordinal) ||
            assemblyName.Contains(':') || assemblyName.Contains('\\') || assemblyName.Contains('/') || assemblyName.Length > 100)
            throw new InvalidDataException("Engine entry assembly must be one local DLL filename.");
        if (ini["id"].Length is < 1 or > 100 || ini["id"].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')) ||
            !Version.TryParse(ini["version"], out _) || ini["entry-type"].Length is < 1 or > 256)
            throw new InvalidDataException("Invalid engine identity, version or entry type.");
        var assembly = Read(Path.Combine(directory, assemblyName), 32 * 1024 * 1024);
        var configurationBytes = Read(Path.Combine(directory, "engine-settings.ini"), 32 * 1024);
        var assemblySha = Hash(assembly); var configurationSha = Hash(configurationBytes);
        if (!ini["assembly-sha256"].Equals(assemblySha, StringComparison.OrdinalIgnoreCase) ||
            !ini["configuration-sha256"].Equals(configurationSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Engine assembly or configuration digest does not match its manifest.");
        var settings = EngineIni.Read(configurationBytes, "settings");
        var info = new EnginePackageInfo(slot, ini["id"], ini["version"], ini["entry-type"], assemblyName,
            assemblySha, configurationSha, Hash(manifestBytes));
        return new(info, assembly, new(configurationSha, new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(settings)));
    }
}

internal sealed class EngineLoadContext() : AssemblyLoadContext("CerebralEngine-" + Guid.NewGuid().ToString("N"), isCollectible: true)
{
    protected override Assembly? Load(AssemblyName name)
    {
        var contract = typeof(IEnginePlugin).Assembly;
        if (name.Name == contract.GetName().Name)
        {
            if (!AssemblyName.ReferenceMatchesDefinition(name, contract.GetName()) || name.Version != contract.GetName().Version)
                throw new InvalidDataException("The engine requires a different shared engine contract.");
            return contract;
        }
        // Initial engine packages contain one managed strategy DLL. Extra/native providers stay feature-owned.
        if (name.Name is "netstandard" or "mscorlib" || name.Name?.StartsWith("System", StringComparison.Ordinal) == true)
            return null;
        throw new FileNotFoundException("Engine private dependencies are not supported by engine protocol 1: " + name.Name);
    }
    protected override nint LoadUnmanagedDll(string name) => throw new NotSupportedException("Native engine dependencies are not supported by engine protocol 1.");
}
