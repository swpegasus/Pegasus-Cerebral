using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Tools;

/// <summary>A selected authoring surface for the host SDK already copied by the scaffold.</summary>
internal static class ServiceSdkReference
{
    internal const string FileName = "SERVICE-SDK.md";
    private const int MaximumBytes = 4096;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static void Write(string root)
    {
        var reference = Create(SdkPath(root));
        File.WriteAllText(Path.Combine(root, "Service", FileName), reference.Card, Utf8);
    }

    internal static ServiceSdkReferenceSummary Inspect(string root)
    {
        var path = Path.Combine(root, "Service", FileName);
        try
        {
            ToolPolicy.RejectLinks(path);
            using var input = File.OpenRead(path);
            var bytes = new byte[MaximumBytes + 1];
            var length = input.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (length > MaximumBytes)
                return new("stale", path, null, null, "Reference exceeds its bound; no exact SDK reference is available.");
            var card = Utf8.GetString(bytes, 0, length);
            var sdk = SdkPath(root);
            ToolPolicy.RejectLinks(sdk);
            var expected = Create(sdk);
            return card == expected.Card
                ? new("available", path, expected.Version, expected.Sha256, null)
                : new("stale", path, expected.Version, expected.Sha256,
                    "Reference differs from the copied SDK authoring surface. Regeneration requires explicit source setup; no files were changed.");
        }
        catch (FileNotFoundException)
        {
            return new("missing", path, null, null, "Reference or copied SDK is missing. Existing projects are not retrofitted automatically.");
        }
        catch (DirectoryNotFoundException)
        {
            return new("missing", path, null, null, "Reference or copied SDK is missing. Existing projects are not retrofitted automatically.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
                                     BadImageFormatException or DecoderFallbackException or InvalidOperationException)
        {
            return new("unavailable", path, null, null, "The selected copied SDK reference could not be validated: " + error.Message);
        }
    }

    private static string SdkPath(string root) => Path.Combine(root, "sdk", "service", "Cerebral.Core.dll");

    private static (string Card, string Version, string Sha256) Create(string sdk)
    {
        // The factory supplied this exact host-owned SDK. Match its metadata to the already
        // loaded public types before reflecting declarations; never load a project assembly.
        using var input = File.OpenRead(sdk);
        if (input.Length > 8 * 1024 * 1024) throw new InvalidDataException("Copied service SDK exceeds 8 MiB.");
        var digest = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        input.Position = 0;
        using var pe = new PEReader(input);
        var metadata = pe.GetMetadataReader();
        var assembly = metadata.GetAssemblyDefinition();
        var loaded = typeof(IServicePlugin).Assembly;
        if (metadata.GetString(assembly.Name) != loaded.GetName().Name ||
            assembly.Version != loaded.GetName().Version ||
            metadata.GetGuid(metadata.GetModuleDefinition().Mvid) != loaded.ManifestModule.ModuleVersionId)
            throw new InvalidDataException("Copied SDK differs from this host's selected SDK; no host-current declaration was substituted.");

        var version = assembly.Version.ToString();
        var text = new StringBuilder()
            .AppendLine("# Service SDK authoring reference")
            .AppendLine("Format: pegasus.cerebral.service.authoring/1")
            .AppendLine($"Contract: {ServicePluginContract.Id}; API: {ServicePluginContract.Version}")
            .AppendLine($"SDK: Cerebral.Core {version}; ../sdk/service/Cerebral.Core.dll")
            .AppendLine($"SHA-256: {digest}")
            .AppendLine("Selected public declarations only; not the whole SDK. Do not recreate SDK types.")
            .AppendLine("Namespaces: Cerebral.Core; System.Text.Json; System.Threading; System.Threading.Tasks.")
            .AppendLine("```csharp");
        var nullability = new NullabilityInfoContext();
        foreach (var type in new[] { typeof(IServicePlugin), typeof(IServicePluginHost), typeof(IServiceCommandExtension), typeof(IToolExtension) })
        {
            text.Append("interface ").Append(type.Name);
            if (type == typeof(IServicePlugin)) text.Append(" : IDisposable");
            text.AppendLine(" {");
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                Property(text, property, nullability);
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(method => !method.IsSpecialName))
                text.Append("  ").Append(TypeName(method.ReturnType, nullability.Create(method.ReturnParameter)))
                    .Append(' ').Append(method.Name).Append('(')
                    .AppendJoin(", ", method.GetParameters().Select(parameter => Parameter(parameter, nullability))).AppendLine(");");
            text.AppendLine("}");
        }
        foreach (var type in new[] { typeof(ToolCall), typeof(ToolDescriptor), typeof(ToolResult), typeof(ToolExecutionEvidence) })
        {
            var constructor = type.GetConstructors().Single();
            var parameters = constructor.GetParameters();
            text.Append(type.Name).Append('(')
                .AppendJoin(", ", parameters.Select(parameter => Parameter(parameter, nullability))).AppendLine(");");
            var positional = parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(property => !positional.Contains(property.Name)))
                Property(text, property, nullability);
        }
        text.Append("enum ToolExecutionOutcome { ").AppendJoin(", ", Enum.GetNames<ToolExecutionOutcome>()).AppendLine(" }")
            .AppendLine("// Host-supplied ToolExecutionContext (selected read members):");
        foreach (var name in new[] { "Profile", "SessionId", "JobId", "WorkingDirectory", "EffectId" })
            Property(text, typeof(ToolExecutionContext).GetProperty(name) ??
                throw new InvalidDataException("The selected context member is unavailable."), nullability);
        text.AppendLine("```")
            .AppendLine("Scalar metrics host: IServiceScalarGenerationHostV1.OpenScalarGenerationObservations() returns an IDisposable IScalarGenerationSubscriptionV1; ReadAsync(CancellationToken) yields ScalarGenerationObservationV1. Dispose the lease.")
            .AppendLine("To require it, use schemaVersion 2 and \"requiredHostFacets\":[\"cerebral.service.scalar-generation/1\"] in service-plugin.json. Omit the member for legacy packages with no facet requirement.")
            .AppendLine("plugin.verify checks built output, not installed host support. Stage and activate reject unavailable facets before changing their selected state; Worker load rejects them before plugin construction.")
            .AppendLine("Hosts predating this facet, including installed603, cannot admit that declaration. Compile against the selected copied SDK and use a coherent Core/Worker/plugin cohort.")
            .AppendLine("IDisposable requires Dispose(). Register extension instances in ToolExtensions; each exposes Tools.")
            .AppendLine($"ToolDescriptor.ModelVisible defaults to {new ToolDescriptor("", "", default, "").ModelVisible.ToString().ToLowerInvariant()}. InputSchema is JsonElement; constructor argument 4 is Capability.")
            .AppendLine("ToolResult argument 3 is bool Truncated. Outcome/provider belong in Evidence (ToolExecutionEvidence).")
            .AppendLine("EffectiveOutcome uses Evidence.Outcome, otherwise Success maps to Succeeded/Failed.")
            .AppendLine("Reference access proves neither model consumption nor behavior. Build/verification do not activate a service.");
        var card = text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        if (Utf8.GetByteCount(card) > MaximumBytes)
            throw new InvalidDataException("Selected service SDK declarations exceed the 4096-byte reference bound.");
        return (card, version, digest);
    }

    private static void Property(StringBuilder text, PropertyInfo property, NullabilityInfoContext nullability)
    {
        text.Append("  ").Append(TypeName(property.PropertyType, nullability.Create(property)))
            .Append(' ').Append(property.Name).Append(" { get;");
        if (property.SetMethod is { } setter)
            text.Append(setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit))
                ? " init;" : " set;");
        text.AppendLine(" }");
    }

    private static string Parameter(ParameterInfo parameter, NullabilityInfoContext nullability)
    {
        var result = TypeName(parameter.ParameterType, nullability.Create(parameter)) + " " + parameter.Name;
        if (!parameter.HasDefaultValue) return result;
        return result + " = " + (parameter.DefaultValue switch
        {
            null => "null",
            bool value => value ? "true" : "false",
            int value => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string value => JsonSerializer.Serialize(value),
            _ => throw new InvalidDataException("Unsupported default in selected service SDK declarations.")
        });
    }

    private static string TypeName(Type type, NullabilityInfo? nullability)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying) return TypeName(underlying, null) + "?";
        var name = type == typeof(string) ? "string" : type == typeof(bool) ? "bool" :
            type == typeof(int) ? "int" : type == typeof(void) ? "void" : type == typeof(object) ? "object" : type.Name;
        if (type.IsGenericType)
            name = name[..name.IndexOf('`')] + "<" + string.Join(", ", type.GetGenericArguments().Select((argument, index) =>
                TypeName(argument, nullability?.GenericTypeArguments.ElementAtOrDefault(index)))) + ">";
        return name + (!type.IsValueType && nullability?.ReadState == NullabilityState.Nullable ? "?" : "");
    }
}

internal sealed record ServiceSdkReferenceSummary(string State, string Path, string? SdkVersion, string? SdkSha256, string? Message);
