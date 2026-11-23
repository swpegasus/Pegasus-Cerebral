using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

internal static class PluginInitializationMetadataTests
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var fixtureRoot = Path.Combine(root, "initialization-metadata");
        Directory.CreateDirectory(fixtureRoot);
        var sdkPath = System.Security.SecurityElement.Escape(typeof(IDesktopPlugin).Assembly.Location);
        var foreignRoot = Path.Combine(fixtureRoot, "Foreign");
        Directory.CreateDirectory(foreignRoot);
        await File.WriteAllTextAsync(Path.Combine(foreignRoot, "Foreign.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><AssemblyName>Cerebral.Desktop.Spoofed</AssemblyName></PropertyGroup>
              <ItemGroup><Reference Include="Cerebral.Desktop.Plugins"><HintPath>{{sdkPath}}</HintPath><Aliases>RealSdk</Aliases></Reference></ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(foreignRoot, "Foreign.cs"), """
            extern alias RealSdk;
            namespace Cerebral.Desktop.Plugins;
            public interface IDesktopPluginHost { }
            public class DesktopPluginBase
            {
                public void Initialize(RealSdk::Cerebral.Desktop.Plugins.IDesktopPluginHost host) { }
            }
            """);
        var project = Path.Combine(fixtureRoot, "InitializationFixtures.csproj");
        await File.WriteAllTextAsync(project, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
              <ItemGroup>
                <Compile Remove="Foreign/**/*.cs" />
                <Reference Include="Cerebral.Desktop.Plugins"><HintPath>{{sdkPath}}</HintPath></Reference>
                <ProjectReference Include="Foreign/Foreign.csproj"><Aliases>ForeignSdk</Aliases></ProjectReference>
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(fixtureRoot, "Entries.cs"), """
            extern alias ForeignSdk;
            using Cerebral.Desktop.Plugins;
            namespace InitializationFixtures;

            public sealed class Inherited : DesktopPluginBase
            {
                public Inherited() => throw new InvalidOperationException("unreviewed constructor must never execute");
            }
            public sealed class BaseFirst : DesktopPluginBase
            {
                public override void Initialize(IDesktopPluginHost host) { base.Initialize(host); _ = Host; }
                private void ForeignHostSeed(ForeignSdk::Cerebral.Desktop.Plugins.IDesktopPluginHost host) { }
                private void OtherSignature(object host) { }
                private T GenericSeed<T>(T value) => value;
            }
            public sealed class Helper : DesktopPluginBase
            {
                public override void Initialize(IDesktopPluginHost host) => InitializeCore(host);
                private void InitializeCore(IDesktopPluginHost host) => base.Initialize(host);
            }
            public sealed class GenericHelper : DesktopPluginBase
            {
                public override void Initialize(IDesktopPluginHost host) => InitializeCore<object>(host);
                private void InitializeCore<T>(IDesktopPluginHost host) => base.Initialize(host);
            }
            public sealed class BaseDelegate : DesktopPluginBase
            {
                public override void Initialize(IDesktopPluginHost host)
                {
                    Action<IDesktopPluginHost> initialize = base.Initialize;
                    initialize(host);
                }
            }
            public sealed class HelperDelegate : DesktopPluginBase
            {
                public override void Initialize(IDesktopPluginHost host)
                {
                    Action<IDesktopPluginHost> initialize = InitializeCore;
                    initialize(host);
                }
                private void InitializeCore(IDesktopPluginHost host) => base.Initialize(host);
            }
            public sealed class RecursiveDelegate : DesktopPluginBase
            {
                public override void Initialize(IDesktopPluginHost host)
                {
                    Action<IDesktopPluginHost> initialize = ((DesktopPluginBase)this).Initialize;
                    initialize(host);
                }
            }
            public sealed class ForeignDelegate : DesktopPluginBase
            {
                public override void Initialize(IDesktopPluginHost host)
                {
                    Action<IDesktopPluginHost> initialize = new ForeignSdk::Cerebral.Desktop.Plugins.DesktopPluginBase().Initialize;
                    initialize(host);
                }
            }
            public class ExplicitHostEntry : DesktopPluginBase, IDesktopPlugin
            {
                public override void Initialize(IDesktopPluginHost host) { }
                void IDesktopPlugin.Initialize(IDesktopPluginHost host) => base.Initialize(host);
            }
            public sealed class InheritedExplicitHostEntry : ExplicitHostEntry
            {
                public override void Initialize(IDesktopPluginHost host) { }
            }
            public class ExplicitMissingHostEntry : DesktopPluginBase, IDesktopPlugin
            {
                public override void Initialize(IDesktopPluginHost host) => base.Initialize(host);
                void IDesktopPlugin.Initialize(IDesktopPluginHost host) { }
            }
            public sealed class InheritedExplicitMissingHostEntry : ExplicitMissingHostEntry
            {
                public override void Initialize(IDesktopPluginHost host) => base.Initialize(host);
            }
            public sealed class ImplicitHostEntry : DesktopPluginBase, IDesktopPlugin
            {
                public new void Initialize(IDesktopPluginHost host) => base.Initialize(host);
            }
            public sealed class ImplicitMissingHostEntry : DesktopPluginBase, IDesktopPlugin
            {
                public new void Initialize(IDesktopPluginHost host) { }
            }
            public abstract class GoodLocalBase : DesktopPluginBase
            {
                public override void Initialize(IDesktopPluginHost host) => base.Initialize(host);
            }
            public sealed class InheritedLocal : GoodLocalBase { }
            public sealed class LocalBaseFirst : GoodLocalBase
            {
                public override void Initialize(IDesktopPluginHost host) => base.Initialize(host);
            }
            public sealed class Missing : DesktopPluginBase
            {
                public override void Initialize(IDesktopPluginHost host) { }
            }
            public sealed class MissingHelper : DesktopPluginBase
            {
                public override void Initialize(IDesktopPluginHost host) => First(host);
                private void First(IDesktopPluginHost host) => Second(host);
                private void Second(IDesktopPluginHost host) => First(host);
            }
            public abstract class BadLocalBase : DesktopPluginBase
            {
                public override void Initialize(IDesktopPluginHost host) { }
            }
            public sealed class InheritedMissing : BadLocalBase { }
            public sealed class LocalMissing : BadLocalBase
            {
                public override void Initialize(IDesktopPluginHost host) => base.Initialize(host);
            }
            public sealed class Recursive : DesktopPluginBase
            {
                public override void Initialize(IDesktopPluginHost host) => ((DesktopPluginBase)this).Initialize(host);
            }
            public sealed class ForeignCall : DesktopPluginBase
            {
                public override void Initialize(IDesktopPluginHost host) => new ForeignSdk::Cerebral.Desktop.Plugins.DesktopPluginBase().Initialize(host);
            }
            public sealed class OtherEntryCall : DesktopPluginBase
            {
                public override void Initialize(IDesktopPluginHost host) => new Helper().Initialize(host);
            }
            public sealed class Hidden : DesktopPluginBase
            {
                public new void Initialize(IDesktopPluginHost host) { }
            }
            public sealed class Conditional : DesktopPluginBase
            {
                public override void Initialize(IDesktopPluginHost host) { if (host is not null) base.Initialize(host); }
            }
            public sealed class Direct : IDesktopPlugin
            {
                public void Initialize(IDesktopPluginHost host) { }
                public IReadOnlyList<DesktopPanel> Panels => [];
                public IReadOnlyList<DesktopAction> ComposerActions => [];
                public DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message) => null;
                public void Dispose() { }
            }
            """);
        var build = await PluginScaffoldTests.BuildAsync(project);
        check(build.ExitCode == 0, "initialization metadata fixtures compile against the exact test SDK without loading their entries" + build.Failure);
        var assembly = Path.Combine(fixtureRoot, "bin", "Release", "net10.0-windows", "InitializationFixtures.dll");
        var manifest = Path.Combine(Path.GetDirectoryName(assembly)!, NativeDesktopPluginPackage.ManifestFileName);
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, contract = NativeDesktopPluginPackage.ContractId, apiVersion = NativeDesktopPluginPackage.ApiVersion,
            id = "fixture.initialization", name = "Initialization metadata", version = "1.0.0", description = "Compiled metadata fixture",
            assembly = Path.GetFileName(assembly), entryType = "InitializationFixtures.Inherited"
        }));
        var identity = NativeDesktopPluginPackage.Inspect(manifest);
        void Accept(string entry, string reason)
        {
            NativeDesktopPluginPackage.ValidateEntryPoint(identity with { EntryType = "InitializationFixtures." + entry });
            check(true, reason);
        }
        void Reject(string entry, string reason, string diagnostic = "base.Initialize(pluginHost)")
        {
            var rejected = false;
            try { NativeDesktopPluginPackage.ValidateEntryPoint(identity with { EntryType = "InitializationFixtures." + entry }); }
            catch (InvalidDataException error) { rejected = error.Message.Contains(diagnostic, StringComparison.Ordinal); }
            check(rejected, reason);
        }
        Accept("Inherited", "SDK initialization is inherited and unreviewed constructor code is not executed");
        Accept("BaseFirst", "an actual nonvirtual exact SDK base-first initialization call is accepted");
        Accept("Helper", "same-entry initialization helpers can reach the exact SDK base call");
        Accept("GenericHelper", "a same-entry generic helper is traversed through its actual method specification");
        Accept("BaseDelegate", "an exact nonvirtual SDK base method address prevents a false omission rejection without claiming invocation proof");
        Accept("HelperDelegate", "a same-entry helper method address is traversed within the existing bounded graph");
        Accept("ExplicitHostEntry", "the exact explicit SDK interface host entry supersedes an unused missing-base public override");
        Accept("InheritedExplicitHostEntry", "an inherited final explicit SDK interface mapping remains the effective host entry");
        Accept("ImplicitHostEntry", "an exact public SDK interface reimplementation can initialize the base independently of the inherited SDK slot");
        Accept("InheritedLocal", "a local base override with actual SDK initialization is inherited");
        Accept("LocalBaseFirst", "a local base-first override reaches the SDK through bounded local inheritance");
        Accept("Direct", "direct IDesktopPlugin implementations retain their independent initialization contract");
        Accept("Hidden", "a hidden new method does not substitute for the inherited SDK virtual initialization slot");
        Accept("Conditional", "missing-call detection accepts a conditional base call without claiming all-path control-flow safety");
        Reject("Missing", "a real SDK override without base initialization is rejected before plugin execution");
        Reject("MissingHelper", "cyclic same-entry helpers with no base call are bounded and rejected");
        Reject("InheritedMissing", "a missing base call in an inherited local override is rejected");
        Reject("LocalMissing", "calling a local base that itself omits SDK initialization cannot fake base-call proof");
        Reject("Recursive", "callvirt to SDK Initialize dispatches the override and cannot fake a nonvirtual base call");
        Reject("RecursiveDelegate", "ldvirtftn to SDK Initialize dispatches the override and cannot fake a nonvirtual base method address");
        Reject("ForeignDelegate", "a foreign same-named SDK method address cannot supply exact SDK initialization provenance");
        Reject("ExplicitMissingHostEntry", "a missing-base effective explicit interface entry is rejected even when the unused public override calls the base");
        Reject("InheritedExplicitMissingHostEntry", "an inherited missing-base explicit interface entry is not replaced by an unused valid public override");
        Reject("ImplicitMissingHostEntry", "a missing-base exact public interface reimplementation is rejected rather than assuming inherited SDK initialization");
        Reject("ForeignCall", "a same-named type and initialization signature from a foreign assembly cannot fake SDK initialization");
        Reject("OtherEntryCall", "initializing another entry instance cannot supply this entry's required base call");

        var original = await File.ReadAllBytesAsync(assembly);
        using (var pe = new PEReader(new MemoryStream(original, writable: false)))
        {
            var metadata = pe.GetMetadataReader();
            var method = metadata.TypeDefinitions.SelectMany(type => metadata.GetTypeDefinition(type).GetMethods()).Single(handle =>
                metadata.GetString(metadata.GetTypeDefinition(metadata.GetMethodDefinition(handle).GetDeclaringType()).Name) == "BaseDelegate" &&
                metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "Initialize");
            var member = metadata.MemberReferences.Single(handle =>
            {
                var reference = metadata.GetMemberReference(handle);
                if (metadata.GetString(reference.Name) != "Initialize" || reference.Parent.Kind != HandleKind.TypeReference) return false;
                var parent = metadata.GetTypeReference((TypeReferenceHandle)reference.Parent);
                return metadata.GetString(parent.Name) == "DesktopPluginBase" && parent.ResolutionScope.Kind == HandleKind.AssemblyReference &&
                       metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)parent.ResolutionScope).Name) == "Cerebral.Desktop.Plugins";
            });
            var address = new byte[6];
            address[0] = 0xfe; address[1] = 0x06;
            BinaryPrimitives.WriteInt32LittleEndian(address.AsSpan(2), MetadataTokens.GetToken(member));
            check(pe.GetMethodBody(metadata.GetMethodDefinition(method).RelativeVirtualAddress).GetILBytes()!.AsSpan().IndexOf(address) >= 0,
                "the real compiler emits ldftn of the exact SDK base Initialize for the delegate regression");
        }
        async Task Tamper(string reason, Action<byte[], PEReader, MetadataReader, MemberReferenceHandle, MethodDefinitionHandle> change)
        {
            var bytes = original.ToArray();
            using (var pe = new PEReader(new MemoryStream(bytes, writable: false)))
            {
                var metadata = pe.GetMetadataReader();
                var method = metadata.TypeDefinitions.SelectMany(type => metadata.GetTypeDefinition(type).GetMethods()).Single(handle =>
                    metadata.GetString(metadata.GetTypeDefinition(metadata.GetMethodDefinition(handle).GetDeclaringType()).Name) == "BaseFirst" &&
                    metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "Initialize");
                var member = metadata.MemberReferences.Single(handle =>
                {
                    var reference = metadata.GetMemberReference(handle);
                    if (metadata.GetString(reference.Name) != "Initialize" || reference.Parent.Kind != HandleKind.TypeReference) return false;
                    var parent = metadata.GetTypeReference((TypeReferenceHandle)reference.Parent);
                    return metadata.GetString(parent.Name) == "DesktopPluginBase" && parent.ResolutionScope.Kind == HandleKind.AssemblyReference &&
                           metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)parent.ResolutionScope).Name) == "Cerebral.Desktop.Plugins";
                });
                change(bytes, pe, metadata, member, method);
            }
            await File.WriteAllBytesAsync(assembly, bytes);
            try { Reject("BaseFirst", reason, "initialization"); }
            finally { await File.WriteAllBytesAsync(assembly, original); }
        }
        await Tamper("a base call targeted at a same-named foreign SDK type is rejected", (bytes, pe, metadata, member, _) =>
        {
            var foreign = metadata.TypeReferences.Single(handle =>
            {
                var type = metadata.GetTypeReference(handle);
                return metadata.GetString(type.Name) == "DesktopPluginBase" && type.ResolutionScope.Kind == HandleKind.AssemblyReference &&
                       metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name) == "Cerebral.Desktop.Spoofed";
            });
            SetMemberParent(bytes, pe, metadata, member, foreign);
        });
        await Tamper("a base-call declaration on the wrong exact SDK type is rejected", (bytes, pe, metadata, member, _) =>
        {
            var host = metadata.TypeReferences.Single(handle =>
            {
                var type = metadata.GetTypeReference(handle);
                return metadata.GetString(type.Name) == "IDesktopPluginHost" && type.ResolutionScope.Kind == HandleKind.AssemblyReference &&
                       metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name) == "Cerebral.Desktop.Plugins";
            });
            SetMemberParent(bytes, pe, metadata, member, host);
        });
        foreach (var seed in new[] { "ForeignHostSeed", "OtherSignature", "GenericSeed" })
            await Tamper("a base-call declaration using " + seed + " cannot supply the exact SDK method signature", (bytes, pe, metadata, member, _) =>
            {
                var signature = metadata.MethodDefinitions.Select(metadata.GetMethodDefinition).Single(method =>
                    metadata.GetString(method.Name) == seed).Signature;
                SetMemberSignature(bytes, pe, metadata, member, signature);
            });
        await Tamper("callvirt to the SDK declaration is rejected even with an otherwise exact target", (bytes, pe, metadata, member, method) =>
        {
            var offset = CallOffset(bytes, pe, metadata, member, method);
            bytes[offset] = 0x6f;
        });
        await Tamper("an invalid initialization call token fails closed as malformed IL", (bytes, pe, metadata, member, method) =>
        {
            var offset = CallOffset(bytes, pe, metadata, member, method);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 1, 4), 0x70000001);
        });
        await Tamper("a generic instantiation of nongeneric SDK initialization fails closed instead of faking a base call", (bytes, pe, metadata, member, method) =>
        {
            var specification = MetadataTokens.MethodSpecificationHandle(1);
            _ = metadata.GetMethodSpecification(specification);
            var rowSize = metadata.GetTableRowSize(TableIndex.MethodSpec);
            var blobSize = metadata.GetHeapSize(HeapIndex.Blob) > ushort.MaxValue ? 4 : 2;
            var specificationOffset = pe.PEHeaders.MetadataStartOffset + metadata.GetTableMetadataOffset(TableIndex.MethodSpec);
            WriteIndex(bytes, specificationOffset, rowSize - blobSize, ((uint)MetadataTokens.GetRowNumber(member) << 1) | 1u);
            var offset = CallOffset(bytes, pe, metadata, member, method);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 1, 4), MetadataTokens.GetToken(specification));
        });
        Accept("BaseFirst", "restoring exact compiled bytes restores admission after malformed/foreign metadata fixtures");
    }

    private static void SetMemberParent(byte[] bytes, PEReader pe, MetadataReader metadata, MemberReferenceHandle member, TypeReferenceHandle parent)
    {
        var rowSize = metadata.GetTableRowSize(TableIndex.MemberRef);
        var stringSize = metadata.GetHeapSize(HeapIndex.String) > ushort.MaxValue ? 4 : 2;
        var blobSize = metadata.GetHeapSize(HeapIndex.Blob) > ushort.MaxValue ? 4 : 2;
        var size = rowSize - stringSize - blobSize;
        var offset = pe.PEHeaders.MetadataStartOffset + metadata.GetTableMetadataOffset(TableIndex.MemberRef) +
                     (MetadataTokens.GetRowNumber(member) - 1) * rowSize;
        WriteIndex(bytes, offset, size, ((uint)MetadataTokens.GetRowNumber(parent) << 3) | 1u);
    }

    private static void SetMemberSignature(byte[] bytes, PEReader pe, MetadataReader metadata, MemberReferenceHandle member, BlobHandle signature)
    {
        var rowSize = metadata.GetTableRowSize(TableIndex.MemberRef);
        var size = metadata.GetHeapSize(HeapIndex.Blob) > ushort.MaxValue ? 4 : 2;
        var offset = pe.PEHeaders.MetadataStartOffset + metadata.GetTableMetadataOffset(TableIndex.MemberRef) +
                     MetadataTokens.GetRowNumber(member) * rowSize - size;
        WriteIndex(bytes, offset, size, (uint)MetadataTokens.GetHeapOffset(signature));
    }

    private static void WriteIndex(byte[] bytes, int offset, int size, uint value)
    {
        if (size == 2) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), checked((ushort)value));
        else BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    }

    private static int CallOffset(byte[] bytes, PEReader pe, MetadataReader metadata, MemberReferenceHandle member, MethodDefinitionHandle method)
    {
        var rva = metadata.GetMethodDefinition(method).RelativeVirtualAddress;
        var section = pe.PEHeaders.SectionHeaders.Single(header => rva >= header.VirtualAddress && rva < header.VirtualAddress + header.SizeOfRawData);
        var offset = section.PointerToRawData + rva - section.VirtualAddress;
        var headerSize = (bytes[offset] & 3) == 2 ? 1 : (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2)) >> 12) * 4;
        var pattern = new byte[5];
        pattern[0] = 0x28;
        BinaryPrimitives.WriteInt32LittleEndian(pattern.AsSpan(1), MetadataTokens.GetToken(member));
        var call = pe.GetMethodBody(rva).GetILBytes()!.AsSpan().IndexOf(pattern);
        if (call < 0) throw new InvalidOperationException("The compiled fixture must contain an actual SDK base call.");
        return offset + headerSize + call;
    }
}
