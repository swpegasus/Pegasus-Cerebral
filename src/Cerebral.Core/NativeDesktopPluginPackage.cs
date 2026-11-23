using System.Text.Json;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;

namespace Cerebral.Core;

/// <summary>
/// Executable-code-free admission for supported native desktop package manifests. The Worker and desktop
/// use this same boundary before a package can be queued, installed, or loaded.
/// </summary>
public static class NativeDesktopPluginPackage
{
    public const string ManifestFileName = "desktop-plugin.json";
    public const string ContractId = "pegasus.cerebral.desktop";
    public const int ApiVersion = 1;
    public const int MaximumEntries = 2048;
    public const int MaximumDirectories = 512;
    public const int MaximumDepth = 16;
    public const long MaximumExpandedBytes = 4L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> Version1Fields =
    ["schemaVersion", "contract", "apiVersion", "id", "name", "version", "description", "assembly", "entryType"];
    private static readonly HashSet<string> Version2Fields =
    [.. Version1Fields, "dependencies"];
    private static readonly HashSet<string> Version3Fields =
    [.. Version2Fields, "defaultEnabled", "exclusiveGroup", "exclusivePriority"];
    private const int MaximumConstructorMethods = 256;
    private const int MaximumMethodIlBytes = 1_048_576;
    private const int MaximumConstructorInstructions = 65_536;
    private static readonly IReadOnlyDictionary<ushort, OperandType> IlOperandTypes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .GroupBy(opcode => unchecked((ushort)opcode.Value))
        .ToDictionary(group => group.Key, group => group.First().OperandType);

    public static NativeDesktopPluginIdentity Inspect(string manifestPath)
    {
        var path = Path.GetFullPath(manifestPath);
        if (!Path.GetFileName(path).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select the package's exact desktop-plugin.json file.");
        RejectLinks(path);
        using var input = File.OpenRead(path);
        if (input.Length is < 2 or > 65_536) throw new InvalidDataException("Native plugin manifests must contain a JSON object of at most 64 KiB.");
        using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 16, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The native plugin manifest must be a JSON object.");
        var properties = root.EnumerateObject().ToArray();
        if (properties.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length ||
            !root.TryGetProperty("schemaVersion", out var schemaValue) || schemaValue.ValueKind != JsonValueKind.Number ||
            !schemaValue.TryGetInt32(out var schema) || schema is not (1 or 2 or 3))
            throw new InvalidDataException("The native plugin manifest requires a supported schemaVersion. Version 1 defines identity and entrypoint fields; version 2 adds dependencies; version 3 adds bundled default and exclusive-provider fields.");
        var expectedFields = schema switch { 1 => Version1Fields, 2 => Version2Fields, _ => Version3Fields };
        if (!properties.Select(x => x.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expectedFields))
            throw new InvalidDataException(schema switch
            {
                1 => "The native plugin manifest must contain exactly these version 1 fields once each: schemaVersion, contract, apiVersion, id, name, version, description, assembly, entryType. Do not invent panel fields. Edit the source desktop-plugin.json and rebuild; do not edit bin or obj output directly.",
                2 => "The native plugin manifest must contain exactly the version 1 fields plus dependencies for schema version 2. Do not invent package or panel fields.",
                _ => "The native plugin manifest must contain exactly the version 2 fields plus defaultEnabled, exclusiveGroup, and exclusivePriority for schema version 3."
            });

        var api = Integer(root, "apiVersion");
        var contract = Text(root, "contract"); var id = Text(root, "id"); var name = Text(root, "name");
        var version = Text(root, "version"); var description = Text(root, "description", allowEmpty: true);
        var assembly = Text(root, "assembly"); var entryType = Text(root, "entryType");
        if (api != ApiVersion || contract != ContractId)
            throw new InvalidDataException("The native plugin requires an unsupported manifest or desktop API version.");
        ValidateId(id);
        if (name.Length > 100 || description.Length > 2000 || !Version.TryParse(version, out _) || entryType.Length > 256)
            throw new InvalidDataException("The native plugin identity, version or entry type is invalid.");
        if (assembly.Contains('/') || assembly.Contains('\\') || !Path.GetFileName(assembly).Equals(assembly, StringComparison.Ordinal))
            throw new InvalidDataException("The native plugin assembly must be the adjacent DLL filename in the same directory as desktop-plugin.json. Present the manifest copied to the build output; do not point a source manifest into bin or obj.");
        var assemblyPath = ContainedPath(Path.GetDirectoryName(path)!, assembly);
        if (!assemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !File.Exists(assemblyPath))
            throw new InvalidDataException("The adjacent native plugin assembly is missing or is not a DLL. Present the desktop-plugin.json copied beside the built DLL.");
        RejectLinks(assemblyPath);
        var dependencies = schema >= 2 ? Dependencies(root.GetProperty("dependencies")) : [];
        var defaultEnabled = schema == 3 ? Boolean(root, "defaultEnabled") : true;
        var exclusiveGroup = schema == 3 ? Text(root, "exclusiveGroup") : null;
        var exclusivePriority = schema == 3 ? Integer(root, "exclusivePriority") : 0;
        if (exclusiveGroup is not null) ValidateId(exclusiveGroup);
        if (exclusivePriority is < 0 or > 1000)
            throw new InvalidDataException("Native plugin exclusivePriority must be between 0 and 1000.");
        return new(schema, id, name, version, description, assembly, entryType, assemblyPath, dependencies,
            defaultEnabled, exclusiveGroup, exclusivePriority);
    }

    public static void ValidateId(string id)
    {
        if (id is null || !Regex.IsMatch(id, "^[a-z0-9](?:[a-z0-9._-]{0,62}[a-z0-9_-])?$", RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)) ||
            Regex.IsMatch(id, "^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:[.]|$)", RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)))
            throw new InvalidDataException("Native plugin IDs use 1–64 lowercase letters, digits, dots, underscores or hyphens.");
    }

    /// <summary>Content identity for the exact admitted package file set. Relative names and bytes
    /// participate; source/installation paths, timestamps, and executable plugin code do not.</summary>
    public static string ComputeFingerprint(string manifestPath)
    {
        var path = Path.GetFullPath(manifestPath);
        _ = Inspect(path);
        var root = Path.GetDirectoryName(path)!;
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        fingerprint.AppendData("pegasus.cerebral.native-desktop-package\0files-v1\0"u8);
        Span<byte> number = stackalloc byte[8];
        var files = EnumeratePackageFiles(root).Select(file =>
            (Path: file, Name: Path.GetRelativePath(root, file).Replace('\\', '/')))
            .OrderBy(file => file.Name, StringComparer.Ordinal).ToArray();
        if (files.Select(file => file.Name).Distinct(StringComparer.Ordinal).Count() != files.Length)
            throw new InvalidDataException("The native plugin package contains ambiguous normalized relative filenames.");
        BinaryPrimitives.WriteInt64LittleEndian(number, files.Length); fingerprint.AppendData(number);
        long bytes = 0;
        foreach (var file in files)
        {
            RejectLinks(file.Path);
            var name = Encoding.UTF8.GetBytes(file.Name);
            BinaryPrimitives.WriteInt64LittleEndian(number, name.Length); fingerprint.AppendData(number);
            fingerprint.AppendData(name);
            using var input = File.OpenRead(file.Path);
            var length = input.Length;
            bytes = checked(bytes + length);
            if (bytes > MaximumExpandedBytes)
                throw new IOException("The native plugin package grew beyond its admitted byte limit while being fingerprinted.");
            BinaryPrimitives.WriteInt64LittleEndian(number, length); fingerprint.AppendData(number);
            fingerprint.AppendData(SHA256.HashData(input));
            if (input.Length != length)
                throw new IOException("The native plugin package changed while being fingerprinted. Rebuild and review it again.");
        }
        return Convert.ToHexString(fingerprint.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>The bounded regular-file set copied into an immutable desktop package generation.</summary>
    public static IReadOnlyList<string> EnumeratePackageFiles(string root)
    {
        root = Path.GetFullPath(root); RejectLinks(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var result = new List<string>(); var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0));
        long bytes = 0; var directoryCount = 0;
        while (pending.TryPop(out var directory))
        {
            RejectLinks(directory.Path);
            if (directory.Depth > MaximumDepth || ++directoryCount > MaximumDirectories)
                throw new IOException($"Native plugin packages are limited to {MaximumDirectories:N0} folders and {MaximumDepth} path levels.");
            foreach (var file in Directory.EnumerateFileSystemEntries(directory.Path))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Native plugin packages cannot contain links or junctions.");
                if (Directory.Exists(file)) { pending.Push((file, directory.Depth + 1)); continue; }
                bytes += new FileInfo(file).Length; result.Add(file);
                if (result.Count > MaximumEntries || bytes > MaximumExpandedBytes)
                    throw new IOException($"Native plugin packages are limited to {MaximumEntries:N0} files and {MaximumExpandedBytes / 1048576:N0} MiB.");
            }
        }
        return result;
    }

    /// <summary>Check lifecycle metadata without loading or executing unreviewed plugin code.</summary>
    public static void ValidateEntryPoint(NativeDesktopPluginIdentity identity)
    {
        try { ValidateEntryPointMetadata(identity); }
        catch (BadImageFormatException error)
        {
            // Malformed PE headers are unreviewed package data, not a host failure.
            throw new InvalidDataException("The desktop assembly contains malformed managed metadata and cannot be verified. Rebuild the plugin and review the new package.", error);
        }
    }

    private static void ValidateEntryPointMetadata(NativeDesktopPluginIdentity identity)
    {
        using var input = File.OpenRead(identity.AssemblyPath);
        using var executable = new PEReader(input);
        if (!executable.HasMetadata) throw new InvalidDataException("The desktop assembly has no managed entrypoint metadata.");
        var metadata = executable.GetMetadataReader();
        string TypeName(TypeDefinitionHandle handle)
        {
            // Metadata is unreviewed input. Never recurse through an unbounded or cyclic
            // declaring-type chain while inspecting an assembly without executing it.
            var names = new List<string>();
            var visited = new HashSet<TypeDefinitionHandle>();
            while (!handle.IsNil)
            {
                if (names.Count == 256 || !visited.Add(handle))
                    throw new InvalidDataException("The desktop assembly contains an invalid declaring-type chain.");
                var definition = metadata.GetTypeDefinition(handle);
                names.Add(metadata.GetString(definition.Name));
                var parent = definition.GetDeclaringType();
                if (parent.IsNil)
                {
                    names.Reverse();
                    var name = string.Join("+", names);
                    var space = metadata.GetString(definition.Namespace);
                    return space.Length == 0 ? name : space + "." + name;
                }
                handle = parent;
            }
            throw new InvalidDataException("The desktop assembly contains an invalid declaring-type chain.");
        }
        foreach (var handle in metadata.TypeDefinitions)
        {
            if (TypeName(handle) != identity.EntryType) continue;
            var type = metadata.GetTypeDefinition(handle);
            var visibility = type.Attributes & TypeAttributes.VisibilityMask;
            if (visibility is not (TypeAttributes.Public or TypeAttributes.NestedPublic) ||
                (type.Attributes & (TypeAttributes.Abstract | TypeAttributes.Interface)) != 0 ||
                type.GetGenericParameters().Count != 0)
                throw new InvalidDataException("The desktop entry type must be a public, concrete, closed class.");
            foreach (var methodHandle in type.GetMethods())
            {
                var method = metadata.GetMethodDefinition(methodHandle);
                if (metadata.GetString(method.Name) != ".ctor" ||
                    (method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public ||
                    (method.Attributes & MethodAttributes.Static) != 0) continue;
                var signature = metadata.GetBlobReader(method.Signature);
                var header = signature.ReadSignatureHeader();
                if (!header.IsGeneric && signature.ReadCompressedInteger() == 0)
                {
                    ValidatePreInitializationHostAccess(executable, metadata, handle, methodHandle);
                    ValidateBaseInitialization(executable, metadata, handle);
                    return;
                }
            }
            throw new InvalidDataException("The desktop entry type requires a public parameterless constructor. The host calls Initialize(pluginHost) after construction; constructor dependency injection is unsupported. Repair the source and rebuild.");
        }
        throw new InvalidDataException("The desktop manifest entryType " + JsonSerializer.Serialize(identity.EntryType) +
            " is absent from its built assembly. Restore the source class/namespace to that exact type, or correct the source manifest " +
            "from actual source/assembly evidence; rebuild, then verify again.");
    }

    /// <summary>Follow only direct calls to methods declared by this entry type. This is a lifecycle
    /// check over bounded metadata, not execution or a general code-safety analysis.</summary>
    private static void ValidatePreInitializationHostAccess(PEReader executable, MetadataReader metadata,
        TypeDefinitionHandle entryType, MethodDefinitionHandle constructor)
    {
        var pending = new Stack<MethodDefinitionHandle>();
        var visited = new HashSet<MethodDefinitionHandle>();
        pending.Push(constructor);
        var instructions = 0;
        try
        {
            while (pending.Count != 0)
            {
                var methodHandle = pending.Pop();
                if (!visited.Add(methodHandle)) continue;
                if (visited.Count > MaximumConstructorMethods)
                    throw new InvalidDataException("The desktop entry constructor's same-type initialization graph exceeds the metadata inspection limit. Keep constructor initialization small and host-independent.");
                foreach (var called in ReadDirectCalls(executable, metadata, methodHandle, methodHandle == constructor, ref instructions))
                {
                    var target = UnwrapMethodSpecification(metadata, called.Target);
                    if (IsPreInitializationHostAccess(metadata, target))
                        throw new InvalidDataException("The desktop entry constructor reaches DesktopPluginBase.Host or Environment before Initialize. Keep the public parameterless constructor host-independent; use Host or Environment only in Initialize after calling base.Initialize(pluginHost), then rebuild.");
                    if (target.Kind == HandleKind.MethodDefinition)
                    {
                        var targetMethod = (MethodDefinitionHandle)target;
                        if (metadata.GetMethodDefinition(targetMethod).GetDeclaringType() == entryType)
                            pending.Push(targetMethod);
                    }
                }
            }
        }
        catch (Exception error) when (error is BadImageFormatException or ArgumentException)
        {
            throw new InvalidDataException("The desktop entry constructor contains malformed managed IL metadata and cannot be verified safely.", error);
        }
    }

    // This detects definite absence only in a proven effective host entry. Exact
    // nonvirtual calls or method addresses prevent an omission rejection; neither
    // proves invocation, every-path control flow, receiver/argument identity, or safety.
    private static void ValidateBaseInitialization(PEReader executable, MetadataReader metadata,
        TypeDefinitionHandle entryType)
    {
        try
        {
            var chain = new List<TypeDefinitionHandle>();
            var types = new HashSet<TypeDefinitionHandle>();
            var current = entryType;
            while (true)
            {
                if (!types.Add(current) || types.Count > MaximumConstructorMethods)
                    throw new InvalidDataException("The desktop entry has a cyclic or oversized local initialization inheritance graph.");
                chain.Add(current);
                var baseType = metadata.GetTypeDefinition(current).BaseType;
                if (IsDesktopSdkType(metadata, baseType, "DesktopPluginBase")) break;
                // Only a proven SDK base chain is subject to this missing-call check.
                // Direct IDesktopPlugin entries retain their independent initialization.
                if (baseType.Kind != HandleKind.TypeDefinition) return;
                current = (TypeDefinitionHandle)baseType;
            }

            MethodDefinitionHandle initialization = default;
            MethodDefinitionHandle interfaceInitialization = default;
            var interfaceUsesSdkSlot = false;
            var interfaceEntryUnproven = false;
            var nearestSdkSlot = true;
            var inspected = 0;
            for (var index = chain.Count - 1; index >= 0; index--)
            {
                var definition = metadata.GetTypeDefinition(chain[index]);
                var reimplementsInterface = false;
                foreach (var interfaceHandle in definition.GetInterfaceImplementations())
                {
                    if (++inspected > MaximumConstructorInstructions)
                        throw new InvalidDataException("The desktop entry initialization metadata exceeds the inspection limit.");
                    if (!IsDesktopSdkType(metadata, metadata.GetInterfaceImplementation(interfaceHandle).Interface, "IDesktopPlugin")) continue;
                    reimplementsInterface = true;
                    interfaceInitialization = default;
                    interfaceUsesSdkSlot = false;
                    // An interface reimplementation without an exact local mapping is
                    // outside this omission check, rather than an assumed SDK slot.
                    interfaceEntryUnproven = true;
                }
                foreach (var methodHandle in definition.GetMethods())
                {
                    if (++inspected > MaximumConstructorInstructions)
                        throw new InvalidDataException("The desktop entry initialization metadata exceeds the inspection limit.");
                    var method = metadata.GetMethodDefinition(methodHandle);
                    if (metadata.GetString(method.Name) != "Initialize" ||
                        (method.Attributes & MethodAttributes.Static) != 0 ||
                        !IsDesktopInitializationSignature(metadata, method.Signature)) continue;
                    if (reimplementsInterface && (method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public)
                    {
                        if (!interfaceInitialization.IsNil)
                            throw new BadImageFormatException("The desktop entry has ambiguous SDK interface initialization methods.");
                        interfaceInitialization = methodHandle;
                        interfaceUsesSdkSlot = nearestSdkSlot &&
                            (method.Attributes & (MethodAttributes.Virtual | MethodAttributes.NewSlot)) == MethodAttributes.Virtual;
                        interfaceEntryUnproven = index != 0 && !interfaceUsesSdkSlot &&
                            (method.Attributes & MethodAttributes.Virtual) != 0 &&
                            (method.Attributes & MethodAttributes.Final) == 0;
                    }
                    if ((method.Attributes & MethodAttributes.Virtual) == 0 ||
                        (method.Attributes & MethodAttributes.NewSlot) != 0)
                        nearestSdkSlot = false;
                    else if (nearestSdkSlot)
                        initialization = methodHandle;
                }
                var interfaceMapping = false;
                foreach (var implementationHandle in definition.GetMethodImplementations())
                {
                    if (++inspected > MaximumConstructorInstructions)
                        throw new InvalidDataException("The desktop entry initialization metadata exceeds the inspection limit.");
                    var implementation = metadata.GetMethodImplementation(implementationHandle);
                    var interfaceDeclaration = IsDesktopInitializationMember(metadata, implementation.MethodDeclaration, "IDesktopPlugin");
                    if (!interfaceDeclaration && !IsDesktopBaseInitialization(metadata, implementation.MethodDeclaration)) continue;
                    if (implementation.MethodBody.Kind != HandleKind.MethodDefinition)
                        throw new BadImageFormatException("The desktop entry has an invalid SDK initialization mapping body.");
                    var body = (MethodDefinitionHandle)implementation.MethodBody;
                    var method = metadata.GetMethodDefinition(body);
                    if (method.GetDeclaringType() != chain[index] ||
                        (method.Attributes & (MethodAttributes.Static | MethodAttributes.Virtual)) != MethodAttributes.Virtual ||
                        !IsDesktopInitializationSignature(metadata, method.Signature))
                        throw new BadImageFormatException("The desktop entry has an invalid SDK initialization override mapping.");
                    if (interfaceDeclaration)
                    {
                        if (interfaceMapping)
                            throw new BadImageFormatException("The desktop entry has ambiguous SDK interface initialization mappings.");
                        interfaceMapping = true;
                        interfaceInitialization = body;
                        interfaceUsesSdkSlot = false;
                        // A final explicit mapping stays exact through local inheritance.
                        // Nonfinal inherited mappings require broader slot resolution.
                        interfaceEntryUnproven = index != 0 && (method.Attributes & MethodAttributes.Final) == 0;
                    }
                    else initialization = body;
                }
            }
            if (interfaceEntryUnproven) return;
            if (!interfaceInitialization.IsNil && !interfaceUsesSdkSlot) initialization = interfaceInitialization;
            if (initialization.IsNil) return; // The actual SDK initialization is inherited.

            var pending = new Stack<MethodDefinitionHandle>();
            var visited = new HashSet<MethodDefinitionHandle>();
            pending.Push(initialization);
            var instructions = 0;
            var reachesBase = false;
            while (pending.Count != 0)
            {
                var methodHandle = pending.Pop();
                if (!visited.Add(methodHandle)) continue;
                if (visited.Count > MaximumConstructorMethods)
                    throw new InvalidDataException("The desktop entry's initialization call graph exceeds the metadata inspection limit. Keep initialization small and call base.Initialize(pluginHost).");
                foreach (var called in ReadDirectCalls(executable, metadata, methodHandle, methodHandle == initialization, ref instructions, includeMethodAddresses: true))
                {
                    var target = UnwrapMethodSpecification(metadata, called.Target);
                    // callvirt/ldvirtftn would dispatch an override. An exact ldftn only
                    // prevents definite absence; it is not proof the delegate is invoked.
                    if (IsDesktopBaseInitialization(metadata, target))
                    {
                        if (called.Target.Kind == HandleKind.MethodSpecification)
                            throw new BadImageFormatException("The nongeneric SDK initialization method has an invalid generic call specification.");
                        if (!called.Virtual) reachesBase = true;
                    }
                    if (target.Kind == HandleKind.MethodDefinition)
                    {
                        var targetMethod = (MethodDefinitionHandle)target;
                        if (types.Contains(metadata.GetMethodDefinition(targetMethod).GetDeclaringType()))
                            pending.Push(targetMethod);
                    }
                }
            }
            if (!reachesBase)
                throw new InvalidDataException("The desktop entry's effective Initialize has no nonvirtual call or method-address reference to SDK DesktopPluginBase initialization in its bounded local graph. Call base.Initialize(pluginHost) in that entry or a same-type helper before using Host or Environment, then rebuild and verify. Metadata admission does not prove invocation or control-flow safety.");
        }
        catch (Exception error) when (error is BadImageFormatException or ArgumentException)
        {
            throw new InvalidDataException("The desktop entry initialization contains malformed managed metadata or IL and cannot be verified safely.", error);
        }
    }

    private static bool IsDesktopBaseInitialization(MetadataReader metadata, EntityHandle handle) =>
        IsDesktopInitializationMember(metadata, handle, "DesktopPluginBase");

    private static bool IsDesktopInitializationMember(MetadataReader metadata, EntityHandle handle, string typeName)
    {
        if (handle.Kind != HandleKind.MemberReference) return false;
        var member = metadata.GetMemberReference((MemberReferenceHandle)handle);
        return member.GetKind() == MemberReferenceKind.Method &&
               metadata.GetString(member.Name) == "Initialize" &&
               IsDesktopSdkType(metadata, member.Parent, typeName) &&
               IsDesktopInitializationSignature(metadata, member.Signature);
    }

    private static bool IsDesktopInitializationSignature(MetadataReader metadata, BlobHandle blob)
    {
        var signature = metadata.GetBlobReader(blob);
        // Canonical instance void Initialize(class IDesktopPluginHost), with no
        // generic, vararg, byref, modifier, overload, or foreign-type substitution.
        return signature.ReadSignatureHeader().RawValue == 0x20 &&
               signature.ReadCompressedInteger() == 1 && signature.ReadByte() == 0x01 &&
               signature.ReadByte() == 0x12 &&
               IsDesktopSdkType(metadata, signature.ReadTypeHandle(), "IDesktopPluginHost") &&
               signature.RemainingBytes == 0;
    }

    private static bool IsDesktopSdkType(MetadataReader metadata, EntityHandle handle, string name)
    {
        if (handle.Kind != HandleKind.TypeReference) return false;
        var type = metadata.GetTypeReference((TypeReferenceHandle)handle);
        if (metadata.GetString(type.Namespace) != "Cerebral.Desktop.Plugins" ||
            metadata.GetString(type.Name) != name || type.ResolutionScope.Kind != HandleKind.AssemblyReference)
            return false;
        return metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name) ==
               "Cerebral.Desktop.Plugins";
    }

    private readonly record struct DirectCall(EntityHandle Target, bool Virtual);

    private static IReadOnlyList<DirectCall> ReadDirectCalls(PEReader executable, MetadataReader metadata,
        MethodDefinitionHandle methodHandle, bool requireManagedBody, ref int instructions, bool includeMethodAddresses = false)
    {
        var definition = metadata.GetMethodDefinition(methodHandle);
        if (definition.RelativeVirtualAddress == 0)
        {
            if (!requireManagedBody && IsOpaqueRuntimeMethod(metadata, definition)) return [];
            throw new BadImageFormatException(requireManagedBody
                ? "The entry lifecycle method has no managed body."
                : "A lifecycle-reachable managed method has no body.");
        }
        var reader = executable.GetMethodBody(definition.RelativeVirtualAddress).GetILReader();
        if (reader.Length > MaximumMethodIlBytes)
            throw new InvalidDataException("A lifecycle-reachable method exceeds the metadata inspection size limit. Keep entry initialization small.");
        var result = new List<DirectCall>();
        while (reader.RemainingBytes != 0)
        {
            if (++instructions > MaximumConstructorInstructions)
                throw new InvalidDataException("The desktop entry's lifecycle graph exceeds the metadata instruction limit. Keep entry initialization small.");
            var first = reader.ReadByte();
            var value = first == 0xfe
                ? (ushort)(0xfe00 | reader.ReadByte())
                : first;
            if (!IlOperandTypes.TryGetValue(value, out var operandType))
                throw new BadImageFormatException("The constructor contains an unknown IL opcode.");
            if (operandType == OperandType.InlineMethod)
            {
                var token = MetadataTokens.EntityHandle(reader.ReadInt32());
                if (value is 0x28 or 0x6f || includeMethodAddresses && value is 0xfe06 or 0xfe07) // call/callvirt; optional ldftn/ldvirtftn
                {
                    if (token.IsNil || token.Kind is not (HandleKind.MethodDefinition or HandleKind.MemberReference or HandleKind.MethodSpecification))
                        throw new BadImageFormatException("The constructor contains an invalid direct-call target.");
                    result.Add(new(token, value is 0x6f or 0xfe07));
                }
                continue;
            }
            SkipOperand(ref reader, operandType);
        }
        return result;
    }

    private static bool IsOpaqueRuntimeMethod(MetadataReader metadata, MethodDefinition definition)
    {
        if ((definition.Attributes & MethodAttributes.PinvokeImpl) != 0)
        {
            var import = definition.GetImport();
            if (import.Name.IsNil || import.Module.IsNil)
                throw new BadImageFormatException("A constructor-reachable P/Invoke method has invalid import metadata.");
            _ = metadata.GetString(import.Name);
            _ = metadata.GetModuleReference(import.Module);
            return true;
        }
        return (definition.ImplAttributes & MethodImplAttributes.CodeTypeMask) == MethodImplAttributes.Runtime ||
               (definition.ImplAttributes & MethodImplAttributes.InternalCall) != 0;
    }

    private static void SkipOperand(ref BlobReader reader, OperandType operandType)
    {
        var bytes = operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineSig or
                OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => SwitchBytes(ref reader),
            _ => throw new BadImageFormatException("The constructor contains an unsupported IL operand.")
        };
        if (bytes < 0 || bytes > reader.RemainingBytes) throw new BadImageFormatException("The constructor contains a truncated IL operand.");
        reader.Offset += bytes;

        static int SwitchBytes(ref BlobReader reader)
        {
            if (reader.RemainingBytes < sizeof(int)) throw new BadImageFormatException("The constructor contains a truncated IL switch.");
            var cases = reader.ReadInt32();
            if (cases < 0 || cases > MaximumConstructorInstructions) throw new BadImageFormatException("The constructor contains an invalid IL switch.");
            return checked(cases * sizeof(int));
        }
    }

    private static EntityHandle UnwrapMethodSpecification(MetadataReader metadata, EntityHandle handle)
    {
        var target = handle.Kind == HandleKind.MethodSpecification
            ? metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method
            : handle;
        if (target.IsNil || target.Kind is not (HandleKind.MethodDefinition or HandleKind.MemberReference))
            throw new BadImageFormatException("The constructor contains an invalid direct-call target.");
        return target;
    }

    private static bool IsPreInitializationHostAccess(MetadataReader metadata, EntityHandle handle)
    {
        if (handle.Kind != HandleKind.MemberReference) return false;
        var member = metadata.GetMemberReference((MemberReferenceHandle)handle);
        var name = metadata.GetString(member.Name);
        if (member.GetKind() != MemberReferenceKind.Method || name is not ("get_Host" or "get_Environment") ||
            member.Parent.Kind != HandleKind.TypeReference) return false;
        var signature = metadata.GetBlobReader(member.Signature);
        var header = signature.ReadSignatureHeader();
        if (header.Kind != SignatureKind.Method || !header.IsInstance || header.IsGeneric || signature.ReadCompressedInteger() != 0)
            return false;
        var type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
        if (metadata.GetString(type.Namespace) != "Cerebral.Desktop.Plugins" ||
            metadata.GetString(type.Name) != "DesktopPluginBase" ||
            type.ResolutionScope.Kind != HandleKind.AssemblyReference) return false;
        var assembly = metadata.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope);
        return metadata.GetString(assembly.Name) == "Cerebral.Desktop.Plugins";
    }

    public static string ContainedPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Split(['/', '\\']).Any(part => part == ".."))
            throw new InvalidDataException("Plugin assembly paths must be relative to their package.");
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("The plugin assembly path leaves its package.");
        return full;
    }

    private static string Text(JsonElement root, string name, bool allowEmpty = false)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || !allowEmpty && string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("Native plugin manifest field '" + name + "' must be a bounded string.");
        return text;
    }

    private static int Integer(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number :
            throw new InvalidDataException("Native plugin manifest field '" + name + "' must be an integer.");
    }

    private static bool Boolean(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException("Native plugin manifest field '" + name + "' must be a Boolean.")
        };
    }

    private static IReadOnlyList<PluginDependencyDeclaration> Dependencies(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > PluginDependencyPolicy.MaximumDependencies)
            throw new InvalidDataException("A native plugin may declare at most 32 dependencies.");
        var result = new List<PluginDependencyDeclaration>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var allowed = new HashSet<string>(StringComparer.Ordinal)
            { "id", "exactVersion", "minimumVersion", "maximumVersionExclusive", "optional" };
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A native plugin dependency must be an object.");
            var fields = item.EnumerateObject().ToArray();
            if (fields.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length ||
                fields.Any(x => !allowed.Contains(x.Name)) || !item.TryGetProperty("id", out _) || !item.TryGetProperty("optional", out var optional) ||
                optional.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Each native plugin dependency requires id and optional, with only exactVersion or minimumVersion/maximumVersionExclusive constraints.");
            string? OptionalText(string field) => item.TryGetProperty(field, out var property)
                ? property.ValueKind == JsonValueKind.String && property.GetString() is { Length: > 0 } text ? text :
                    throw new InvalidDataException("Dependency field '" + field + "' must be a nonempty string.")
                : null;
            var dependency = new PluginDependencyDeclaration(Text(item, "id"), OptionalText("exactVersion"),
                OptionalText("minimumVersion"), OptionalText("maximumVersionExclusive"), optional.GetBoolean());
            PluginDependencyPolicy.Validate(dependency);
            if (!ids.Add(dependency.Id)) throw new InvalidDataException("Duplicate native plugin dependency: " + dependency.Id);
            result.Add(dependency);
        }
        return result.AsReadOnly();
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Native plugin packages cannot traverse a link or junction.");
    }
}

public sealed record NativeDesktopPluginIdentity(int SchemaVersion, string Id, string Name, string Version, string Description,
    string Assembly, string EntryType, string AssemblyPath, IReadOnlyList<PluginDependencyDeclaration> Dependencies,
    bool DefaultEnabled, string? ExclusiveGroup, int ExclusivePriority);
