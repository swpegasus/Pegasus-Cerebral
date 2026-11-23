using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Cerebral.Engine.Activation;
using Cerebral.Engine.Contracts;

namespace Cerebral.Worker;

/// <summary>Static installer preflight. Never initializes an engine or composes a Worker service.</summary>
internal static class EnginePackageInspection
{
    public static async Task<EngineStatus> InspectAsync(string directory)
    {
        if (directory.Length is < 1 or > 8192 || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Provide one absolute engine directory.");
        var fullPath = Path.GetFullPath(directory);
        if (fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ==
            Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            throw new ArgumentException("Engine directory must be a component directory, not a volume root.");
        var root = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        RejectReparseAncestors(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Engine package directory is missing.");
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0));
        var entries = 0; long bytes = 0;
        while (pending.TryPop(out var current))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(current.Path))
            {
                if (++entries > 1024 || current.Depth >= 16)
                    throw new InvalidDataException("Engine directory exceeds its inspection entry/depth boundary.");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Engine package paths cannot contain reparse points.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (current.Depth == 0 && Path.GetFileName(path) is not ("A" or "B"))
                        throw new InvalidDataException("Engine protocol 1 admits only A and B slot directories.");
                    pending.Push((path, current.Depth + 1));
                }
                else if ((bytes += new FileInfo(path).Length) > 128L * 1024 * 1024)
                    throw new InvalidDataException("Engine directory exceeds its 128 MiB inspection boundary.");
            }
        }

        // Status reuses EnginePackage.ReadSlot (strict INI, hashes, sizes and path
        // checks). Without InitializeAsync it neither loads code nor reads/writes
        // selection state. The unused state path is never opened.
        await using var controller = new EngineController(root, root);
        var status = controller.Status();
        var present = 0;
        foreach (var slot in status.Slots)
        {
            var path = Path.Combine(root, slot.Slot);
            if (!Directory.Exists(path))
            {
                if (File.Exists(path)) throw new InvalidDataException("Engine slot must be a directory: " + slot.Slot);
                continue;
            }
            present++;
            if (!slot.Available || slot.Package is null)
                throw new InvalidDataException("Engine slot " + slot.Slot + " is invalid: " + slot.Error);
            InspectContractReference(Path.Combine(path, slot.Package.AssemblyName), slot.Package.EntryType);
        }
        if (present == 0) throw new InvalidDataException("The engine directory contains no admitted A or B package.");
        return status;
    }

    private static void RejectReparseAncestors(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Engine package paths cannot traverse reparse points.");
        }
    }

    private static void InspectContractReference(string path, string entryType)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var executable = new PEReader(stream);
        if (!executable.HasMetadata) throw new InvalidDataException("Engine entry assembly must contain managed metadata.");
        var metadata = executable.GetMetadataReader();
        if (!metadata.IsAssembly) throw new InvalidDataException("Engine entry must be a managed assembly.");
        string TypeName(TypeDefinitionHandle handle)
        {
            var definition = metadata.GetTypeDefinition(handle);
            var name = metadata.GetString(definition.Name);
            var parent = definition.GetDeclaringType();
            if (!parent.IsNil) return TypeName(parent) + "+" + name;
            var space = metadata.GetString(definition.Namespace);
            return space.Length == 0 ? name : space + "." + name;
        }
        var entry = metadata.TypeDefinitions.FirstOrDefault(handle => TypeName(handle) == entryType);
        if (entry.IsNil) throw new InvalidDataException("Engine manifest entry type is missing from the managed assembly.");
        var type = metadata.GetTypeDefinition(entry);
        if ((type.Attributes & (TypeAttributes.Abstract | TypeAttributes.Interface)) != 0)
            throw new InvalidDataException("Engine manifest entry type must be concrete.");
        var constructor = type.GetMethods().Any(handle =>
        {
            var method = metadata.GetMethodDefinition(handle);
            if (metadata.GetString(method.Name) != ".ctor" ||
                (method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public ||
                (method.Attributes & MethodAttributes.Static) != 0) return false;
            var signature = metadata.GetBlobReader(method.Signature);
            var header = signature.ReadSignatureHeader();
            if (header.IsGeneric) signature.ReadCompressedInteger();
            return signature.ReadCompressedInteger() == 0;
        });
        if (!constructor) throw new InvalidDataException("Engine entry requires a public parameterless constructor.");
        var contract = typeof(IEnginePlugin).Assembly.GetName();
        // Prove the SPI relationship from metadata without loading candidate code.
        // Local base/interface chains are supported; an external base that hides
        // the relationship cannot be safely admitted by this static preflight.
        var pendingTypes = new Stack<EntityHandle>(); pendingTypes.Push(entry);
        var visitedTypes = new HashSet<EntityHandle>();
        var implementsContract = false;
        while (pendingTypes.TryPop(out var handle))
        {
            if (!visitedTypes.Add(handle)) continue;
            if (visitedTypes.Count > 1024)
                throw new InvalidDataException("Engine entry type hierarchy exceeds its inspection boundary.");
            if (handle.Kind == HandleKind.TypeReference)
            {
                var reference = metadata.GetTypeReference((TypeReferenceHandle)handle);
                if (metadata.GetString(reference.Namespace) == typeof(IEnginePlugin).Namespace &&
                    metadata.GetString(reference.Name) == nameof(IEnginePlugin) &&
                    reference.ResolutionScope.Kind == HandleKind.AssemblyReference &&
                    metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name) == contract.Name)
                { implementsContract = true; break; }
            }
            else if (handle.Kind == HandleKind.TypeDefinition)
            {
                var definition = metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
                foreach (var implementation in definition.GetInterfaceImplementations())
                    pendingTypes.Push(metadata.GetInterfaceImplementation(implementation).Interface);
                if (!definition.BaseType.IsNil) pendingTypes.Push(definition.BaseType);
            }
        }
        if (!implementsContract)
            throw new InvalidDataException("Engine entry must declare the shared IEnginePlugin interface in its local type hierarchy.");
        var found = false;
        foreach (var handle in metadata.AssemblyReferences)
        {
            var reference = metadata.GetAssemblyReference(handle);
            if (metadata.GetString(reference.Name) != contract.Name) continue;
            found = true;
            var candidate = new AssemblyName(metadata.GetString(reference.Name))
            {
                Version = reference.Version,
                CultureName = metadata.GetString(reference.Culture)
            };
            var key = metadata.GetBlobBytes(reference.PublicKeyOrToken);
            if ((reference.Flags & AssemblyFlags.PublicKey) != 0) candidate.SetPublicKey(key);
            else candidate.SetPublicKeyToken(key);
            if (!AssemblyName.ReferenceMatchesDefinition(candidate, contract) || candidate.Version != contract.Version ||
                !string.Equals(candidate.CultureName ?? "", contract.CultureName ?? "", StringComparison.OrdinalIgnoreCase) ||
                !(candidate.GetPublicKeyToken() ?? []).SequenceEqual(contract.GetPublicKeyToken() ?? []))
                throw new InvalidDataException("Engine requires a different shared engine contract.");
        }
        if (!found) throw new InvalidDataException("Engine entry assembly does not reference the shared engine contract.");
    }
}
