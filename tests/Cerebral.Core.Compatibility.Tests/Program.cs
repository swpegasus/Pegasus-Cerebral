using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Cerebral.Core;

internal static class Program
{
    private static int checks;

    // Keep all Core-typed code out of this method so the supplied image is loaded before JIT binding.
    public static int Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Pass the exact candidate Cerebral.Core.dll path.");
        var path = Path.GetFullPath(args[0]);
        if (!File.Exists(path) || AssemblyName.GetAssemblyName(path).Name != "Cerebral.Core")
            throw new ArgumentException("The candidate must be an existing Cerebral.Core image.");
        AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        RunInstalledSignatures();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            tier = "isolated CLR constructor/deconstruction calls and JSON schema regression",
            checks,
            candidate = path,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
            installedStateOrDatabaseEffects = 0
        }));
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunInstalledSignatures()
    {
        AssertInstalledSignature(typeof(HostInfo),
            [typeof(string), typeof(string), typeof(int), typeof(string), typeof(string),
                typeof(string), typeof(bool), typeof(string), typeof(bool)]);
        AssertInstalledSignature(typeof(ConfigurationBackupHost),
            [typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int),
                typeof(int), typeof(int), typeof(string), typeof(int), typeof(string), typeof(int?),
                typeof(int?), typeof(int?), typeof(int?), typeof(IReadOnlyList<int>)]);
        AssertInstalledSignature(typeof(ConfigurationBackupInterfacePreferences),
            [typeof(bool?), typeof(bool?), typeof(bool?), typeof(string), typeof(string), typeof(string)]);
        AssertInstalledSignature(typeof(ConfigurationBackupMcp),
            [typeof(string), typeof(string), typeof(string), typeof(bool), typeof(bool), typeof(bool), typeof(bool)]);

        // Build this project against the old installed reference, then execute unchanged against
        // the candidate. These direct calls retain the old constructor and Deconstruct MemberRefs.
        var host = new HostInfo("fixture", "0.2.22", 1, "host", "x64", "Windows", true, @"C:\fixture", false);
        host.Deconstruct(out var product, out var version, out var protocol, out var name, out var architecture,
            out var os, out var coordinator, out var data, out var local);
        Check(product == "fixture" && version == "0.2.22" && protocol == 1 && name == "host", "HostInfo identity");
        Check(architecture == "x64" && os == "Windows" && coordinator && data == @"C:\fixture" && !local, "HostInfo fields");
        Check(Property(host, "InstallationId") is null, "old HostInfo defaults new field");

        int[] processors = [2, 4];
        var backup = new ConfigurationBackupHost(1, 2, 3, 4, 5, 6, 7, 8, 9, "model", 10, "wait",
            11, 12, 13, 14, processors);
        backup.Deconstruct(out var maxJobs, out var queue, out var history, out var historyDays,
            out var activity, out var activityDays, out var reserved, out var idle, out var attempts,
            out var model, out var timeout, out var timeoutAction, out var events, out var megabytes,
            out var eventDays, out var maxProcessors, out var allowed);
        Check(maxJobs == 1 && queue == 2 && history == 3 && historyDays == 4, "backup queue fields");
        Check(activity == 5 && activityDays == 6 && reserved == 7 && idle == 8 && attempts == 9, "backup budget fields");
        Check(model == "model" && timeout == 10 && timeoutAction == "wait", "backup question fields");
        Check(events == 11 && megabytes == 12 && eventDays == 13 && maxProcessors == 14, "backup optional fields");
        Check(ReferenceEquals(allowed, processors), "backup processor collection");
        Check(Equals(Property(backup, "DefaultModelWorkerId"), "local"), "backup default worker");
        Check(Property(backup, "PromptCacheDiagnosticsEnabled") is null &&
              Property(backup, "PromptCacheDiagnosticsChangePercentagePoints") is null, "backup optional new defaults");

        var preferences = new ConfigurationBackupInterfacePreferences(true, false, null, "chat", "dark", "short");
        preferences.Deconstruct(out var enter, out var navigation, out var panels, out var surface, out var theme, out var time);
        Check(enter == true && navigation == false && panels is null, "preferences nullable flags");
        Check(surface == "chat" && theme == "dark" && time == "short", "preferences text");
        Check(Property(preferences, "RecentChatLimit") is null, "preferences new default");

        var mcp = new ConfigurationBackupMcp("fixture", "program.exe", null, true, false, true, true);
        mcp.Deconstruct(out var id, out var executable, out var url, out var connector, out var enabled,
            out var argumentsOmitted, out var endpointOmitted);
        Check(id == "fixture" && executable == "program.exe" && url is null, "MCP identity fields");
        Check(connector && !enabled && argumentsOmitted && endpointOmitted, "MCP omission fields");
        Check(Equals(Property(mcp, "NativeBindingOmitted"), false), "MCP new default");

        // Multiple public constructors must still deserialize through the full, explicitly selected schema.
        var jsonHost = Read<HostInfo>("""{"product":"fixture","version":"0.2.22","protocol":1,"name":"h","architecture":"x64","operatingSystem":"Windows","coordinator":false,"dataDirectory":"fixture","installationId":"new-id"}""");
        Check(Equals(Property(jsonHost, "InstallationId"), "new-id"), "HostInfo extended JSON");
        var jsonBackup = Read<ConfigurationBackupHost>("""{"maxConcurrentJobs":1,"queueCapacity":2,"jobHistoryLimit":3,"jobHistoryDays":4,"activityHistoryLimit":5,"activityHistoryDays":6,"reservedMemoryMb":7,"defaultModelIdleMinutes":8,"maxExecutableAttemptsPerAssignment":9,"defaultModelId":"m","questionTimeoutSeconds":10,"questionTimeoutAction":"wait","defaultModelWorkerId":"remote","promptCacheDiagnosticsEnabled":true,"promptCacheDiagnosticsChangePercentagePoints":23,"workflowIdleExpiryDays":17}""");
        Check(Equals(Property(jsonBackup, "DefaultModelWorkerId"), "remote"), "backup extended worker JSON");
        Check(Equals(Property(jsonBackup, "PromptCacheDiagnosticsEnabled"), true), "backup extended flag JSON");
        Check(Equals(Property(jsonBackup, "PromptCacheDiagnosticsChangePercentagePoints"), 23), "backup extended percentage JSON");
        Check(Equals(Property(jsonBackup, "WorkflowIdleExpiryDays"), 17), "backup non-positional JSON");
        var jsonPreferences = Read<ConfigurationBackupInterfacePreferences>("""{"enterSends":false,"navigationVisible":true,"panelsVisible":false,"selectedSurface":"chat","appearanceTheme":"light","messageTimeFormat":"long","recentChatLimit":37}""");
        Check(Equals(Property(jsonPreferences, "RecentChatLimit"), 37), "preferences extended JSON");
        var jsonMcp = Read<ConfigurationBackupMcp>("""{"id":"m","executable":"p","viaUserConnector":false,"wasEnabled":true,"argumentsOmitted":true,"endpointOmitted":true,"nativeBindingOmitted":true}""");
        Check(Equals(Property(jsonMcp, "NativeBindingOmitted"), true), "MCP extended JSON");

        Check(Read<HostInfo>(JsonSerializer.Serialize(host, CerebralJson.Options)) == host, "old HostInfo roundtrip");
        Check(Read<ConfigurationBackupInterfacePreferences>(JsonSerializer.Serialize(preferences, CerebralJson.Options)) == preferences, "old preferences roundtrip");
        Check(Read<ConfigurationBackupMcp>(JsonSerializer.Serialize(mcp, CerebralJson.Options)) == mcp, "old MCP roundtrip");
        var legacyBackup = Read<ConfigurationBackupHost>(JsonSerializer.Serialize(backup, CerebralJson.Options));
        Check(legacyBackup.MaxConcurrentJobs == 1 && legacyBackup.AllowedLogicalProcessors!.SequenceEqual(processors), "old backup roundtrip");
    }

    private static void AssertInstalledSignature(Type type, Type[] parameters)
    {
        Check(type.GetConstructor(parameters) is not null, type.Name + " exact old CLR constructor");
        var method = type.GetMethod("Deconstruct", parameters.Select(parameter => parameter.MakeByRefType()).ToArray());
        Check(method is not null && method.ReturnType == typeof(void), type.Name + " exact old CLR Deconstruct");
    }

    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, CerebralJson.Options)
        ?? throw new InvalidOperationException("Fixture JSON returned null.");
    private static object? Property(object value, string name) =>
        (value.GetType().GetProperty(name) ?? throw new MissingMemberException(value.GetType().FullName, name)).GetValue(value);
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Core ABI regression: " + name);
        checks++;
    }
}
