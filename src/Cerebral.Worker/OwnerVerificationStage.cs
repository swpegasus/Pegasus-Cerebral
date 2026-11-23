using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Core;
using Microsoft.Win32.SafeHandles;

namespace Cerebral.Worker;

/// <summary>One private, disposable copy of the exact owner-admitted checker closure. Retained
/// read handles and post-run hashes attest bytes; the directory is not an OS sandbox.</summary>
internal sealed class OwnerVerificationStage : IDisposable
{
    internal sealed record Pin(string Role, string Scope, string RelativePath, string OriginalPath,
        string Sha256, long Bytes, string StagedPath);

    private readonly List<FileStream> handles = [];
    private readonly IReadOnlyList<Pin> pins;
    private readonly string closureSchema;
    private readonly string root;
    private bool disposed;

    internal string CheckerRoot { get; }
    internal string TargetRoot { get; }
    internal string ResultsRoot { get; }
    internal string DescriptorPath { get; }
    internal string DescriptorSha256 { get; }
    internal string InputSha256 { get; }
    internal IReadOnlyList<Pin> Pins => pins;

    internal string ClosureDigest(string scope)
    {
        if (scope is not ("checker" or "target")) throw new ArgumentException("Unknown checker closure scope.");
        var body = new StringBuilder(closureSchema).Append('\n').Append(scope).Append('\n');
        foreach (var pin in pins.Where(pin => pin.Scope == scope).OrderBy(pin => pin.Role, StringComparer.Ordinal))
            body.Append(pin.Role).Append('\t').Append(pin.RelativePath).Append('\t')
                .Append(pin.Sha256).Append('\t')
                .Append(pin.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body.ToString()))).ToLowerInvariant();
    }

    private OwnerVerificationStage(string root, string checkerRoot, string targetRoot,
        string resultsRoot, string descriptorPath, IReadOnlyList<Pin> pins,
        List<FileStream> retained, string inputSha256, string descriptorSha256, string closureSchema)
    {
        this.root = root;
        CheckerRoot = checkerRoot;
        TargetRoot = targetRoot;
        ResultsRoot = resultsRoot;
        DescriptorPath = descriptorPath;
        this.pins = pins;
        this.closureSchema = closureSchema;
        handles = retained;
        InputSha256 = inputSha256;
        DescriptorSha256 = descriptorSha256;
    }

    internal static OwnerVerificationStage Prepare(OwnerVerificationAdmission admission)
    {
        admission.Validate();
        var parent = Path.Combine(Path.GetTempPath(), "CerebralOwnerVerification");
        Directory.CreateDirectory(parent);
        RejectReparseAncestors(parent);
        var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checker = Path.Combine(root, "checker");
        var target = Path.Combine(root, "target");
        var results = Path.Combine(root, "results");
        Directory.CreateDirectory(checker);
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(results);
        var handles = new List<FileStream>();
        try
        {
            var pins = new List<Pin>(admission.Files.Count);
            long closureBytes = 0;
            foreach (var file in admission.Files)
            {
                RejectReparseAncestors(file.OriginalPath);
                var original = new FileStream(file.OriginalPath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 65536, FileOptions.SequentialScan);
                handles.Add(original);
                RejectMultipleLinks(original.SafeFileHandle);
                if (original.Length is < 1 or > 64 * 1024 * 1024)
                    throw new InvalidDataException("A verification input is empty or exceeds its byte bound.");
                closureBytes = checked(closureBytes + original.Length);
                if (closureBytes > 128 * 1024 * 1024)
                    throw new InvalidDataException("The verification closure exceeds its total byte bound.");
                var digest = Hash(original);
                if (file.Scope == OwnerVerificationAdmission.Scope.Checker &&
                    (original.Length != file.Bytes || !digest.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("A pinned checker file changed before staging.");
                var stageRoot = file.Scope == OwnerVerificationAdmission.Scope.Checker ? checker : target;
                var staged = Path.Combine(stageRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                original.Position = 0;
                using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 65536, FileOptions.WriteThrough))
                    original.CopyTo(output);
                var retained = new FileStream(staged, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 65536, FileOptions.SequentialScan);
                handles.Add(retained);
                RejectMultipleLinks(retained.SafeFileHandle);
                if (retained.Length != original.Length || Hash(retained) != digest)
                    throw new InvalidDataException("A staged verification file differs from its source.");
                pins.Add(new(file.Role, file.Scope.ToString().ToLowerInvariant(), file.RelativePath,
                    file.OriginalPath, digest, original.Length, staged));
            }
            RequireClosedCode(checker, target, pins);
            var descriptor = Path.Combine(root, "verification.json");
            var descriptorValue = new
            {
                // The pinned checker admits exactly "schema" at the descriptor root.
                schema = admission.CheckerContract.DescriptorSchema,
                checkerVersion = admission.CheckerContract.CheckerVersion,
                checkerRoot = checker,
                targetRoot = target,
                resultsRoot = results,
                files = pins.Select(pin => new
                {
                    role = pin.Role, path = pin.RelativePath,
                    sha256 = pin.Sha256, bytes = pin.Bytes
                }).ToArray()
            };
            var descriptorBytes = JsonSerializer.SerializeToUtf8Bytes(descriptorValue, CerebralJson.Options);
            if (descriptorBytes.Length is < 1 or > 65536)
                throw new InvalidDataException("The checker descriptor exceeds its byte bound.");
            File.WriteAllBytes(descriptor, descriptorBytes);
            handles.Add(new FileStream(descriptor, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536, FileOptions.SequentialScan));
            var descriptorSha = Convert.ToHexString(SHA256.HashData(descriptorBytes)).ToLowerInvariant();
            var identity = OwnerVerificationBudget.InputIdentity(admission.RecipeSha256,
                pins.Where(pin => pin.Scope == "target")
                    .Select(pin => new OwnerVerificationBudget.InputPin(pin.Role, pin.OriginalPath,
                        pin.Sha256, pin.Bytes)).ToArray());
            return new(root, checker, target, results, descriptor, pins, handles, identity,
                descriptorSha, admission.CheckerContract.ClosureSchema);
        }
        catch
        {
            foreach (var handle in handles) handle.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            throw;
        }
    }

    internal void RequireUnchanged()
    {
        if (disposed) throw new ObjectDisposedException(nameof(OwnerVerificationStage));
        RequireClosedCode(CheckerRoot, TargetRoot, pins);
        for (var index = 0; index < pins.Count; index++)
        {
            var pin = pins[index];
            RejectReparseAncestors(pin.OriginalPath);
            RejectReparseAncestors(pin.StagedPath);
            var original = handles[index * 2];
            var staged = handles[index * 2 + 1];
            if (original.Length != pin.Bytes || staged.Length != pin.Bytes ||
                Hash(original) != pin.Sha256 || Hash(staged) != pin.Sha256)
                throw new InvalidDataException("The original or staged verification closure changed.");
        }
        RejectReparseAncestors(DescriptorPath);
        if (handles.Count != pins.Count * 2 + 1 || Hash(handles[^1]) != DescriptorSha256)
            throw new InvalidDataException("The retained checker descriptor changed.");
    }

    internal byte[] ReadSingleReport(string path, string sha256, long bytes)
    {
        if (disposed || bytes is < 1 or > 65536 || sha256 is not { Length: 64 } ||
            sha256.Any(character => !char.IsAsciiHexDigit(character)) ||
            !string.Equals(Path.GetDirectoryName(path), ResultsRoot, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileNameWithoutExtension(path) is not { Length: 32 } name ||
            !name.All(char.IsAsciiHexDigit) || Path.GetExtension(path) != ".json" ||
            Directory.EnumerateFileSystemEntries(ResultsRoot).Count() != 1 ||
            !Directory.EnumerateFileSystemEntries(ResultsRoot).Single().Equals(path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The checker report location or bounded shape is invalid.");
        RejectReparseAncestors(path);
        using var report = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 65536, FileOptions.SequentialScan);
        RejectMultipleLinks(report.SafeFileHandle);
        if (report.Length != bytes) throw new InvalidDataException("The checker report length changed.");
        var body = new byte[checked((int)bytes)];
        report.ReadExactly(body);
        if (!Convert.ToHexString(SHA256.HashData(body)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The checker report hash changed.");
        return body;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var handle in handles)
        {
            try { handle.Dispose(); }
            catch (Exception error) when (error is not OutOfMemoryException) { CleanupPending = true; }
        }
        // Only this instance's random child beneath the verified private parent is removed.
        var parent = Path.Combine(Path.GetTempPath(), "CerebralOwnerVerification");
        if (Path.GetDirectoryName(root) != parent || Path.GetFileName(root).Length != 32)
        { CleanupPending = true; return; }
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (Exception error) when (error is not OutOfMemoryException) { CleanupPending = true; }
    }

    internal bool CleanupPending { get; private set; }

    private static string Hash(FileStream stream)
    {
        stream.Position = 0;
        var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        stream.Position = 0;
        return digest;
    }

    private static void RequireClosedCode(string checker, string target, IReadOnlyList<Pin> pins)
    {
        var expected = pins.Select(pin => pin.StagedPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>([checker, target]);
        var visited = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            RejectReparseAncestors(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++visited > 512) throw new InvalidDataException("Staged verification closure exceeds its entry bound.");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("A staged verification entry is a reparse point.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else if (IsCode(path) && !expected.Contains(path))
                    throw new InvalidDataException("An unlisted staged executable or local-code file is present.");
            }
        }
    }

    private static bool IsCode(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        return name.EndsWith(".dll") || name.EndsWith(".exe") || name.EndsWith(".pdb") ||
            name.EndsWith(".cs") || name.EndsWith(".csproj") || name.EndsWith(".deps.json") ||
            name.EndsWith(".runtimeconfig.json") || name.EndsWith(".so") ||
            name.EndsWith(".dylib") || name.EndsWith(".ps1") || name.EndsWith(".cmd") ||
            name.EndsWith(".bat") || name.EndsWith(".sh") || name.EndsWith(".targets") ||
            name.EndsWith(".props") || name.EndsWith(".config");
    }

    private static void RejectReparseAncestors(string path)
    {
        for (string? cursor = Path.GetFullPath(path); cursor is not null; cursor = Path.GetDirectoryName(cursor))
        {
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A verification input or parent is a reparse point.");
            var next = Path.GetDirectoryName(cursor);
            if (next == cursor) break;
        }
    }

    private static void RejectMultipleLinks(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!GetFileInformationByHandle(handle, out var info) || info.NumberOfLinks != 1)
            throw new InvalidDataException("A verification input has an unverified hard-link identity.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle,
        out ByHandleFileInformation information);
}
