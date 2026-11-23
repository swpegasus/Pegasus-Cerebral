using System.IO;
using System.Runtime.Loader;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Cerebral.ProgrammingPlugin;

internal static class ProgrammingPanelChecksProgram
{
    [STAThread]
    public static int Main(string[] args)
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        try
        {
            if (args.Length == 0) args = [Path.Combine(AppContext.BaseDirectory, "fixtures", "Cerebral.Programming.dll"), Path.Combine(AppContext.BaseDirectory, "fixtures", "Cerebral.ProgrammingPlugin.dll")];
            if (args.Length != 2) throw new ArgumentException("Pass exact service and panel candidate DLL paths.");
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-programming-panel-policy"));
            foreach (var path in new[] { "App.csproj", "Repo.sln", "Repo.slnx", "sub/../App.csproj" })
                Check(ProgrammingDevelopmentDialog.TryFullTarget(root, path) == Path.GetFullPath(path, root), "accepted-root project/solution target normalization");
            foreach (var path in new[] { "", "../foreign.csproj", "../cerebral-programming-panel-policy-other/App.csproj", "settings.json", "Bad\0.csproj", Path.Combine(root, "Absolute.csproj") })
                Check(ProgrammingDevelopmentDialog.TryFullTarget(root, path) is null, "unaccepted or nonrelative target is rejected");
            var serviceId = Constant(args[0], "Cerebral.Programming", "ProgrammingAgentHarness", "Id");
            var serviceVersion = Constant(args[0], "Cerebral.Programming", "ProgrammingAgentHarness", "Version");
            Check(serviceId == Constant(args[1], "Cerebral.ProgrammingPlugin", "ProgrammingPlugin", "DevelopmentWorkflowId"), "compiled service/panel workflow identity matches");
            Check(serviceVersion == Constant(args[1], "Cerebral.ProgrammingPlugin", "ProgrammingPlugin", "DevelopmentWorkflowVersion") && serviceVersion == "1.3.21", "compiled service/panel explicit edition matches");
            AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "Cerebral.Desktop.Plugins.dll"));
            var submissionChecks = ProgrammingPanelSubmissionChecks.Run();
            checks += submissionChecks;
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { success = true, checks, submissionChecks,
                evidenceTier = "deterministic target policy, actual PE constant inspection and WPF-control/fake-host submission fixtures; no window/dialog/renderer or real service submission exercised",
                modelCalls = 0, appCalls = 0, runtimeAdopted = false }));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { success = false, checks, failure = error.GetType().Name, error.Message }));
            return 1;
        }
    }
    private static string Constant(string path, string ns, string typeName, string name)
    {
        using var stream = File.OpenRead(path); using var pe = new PEReader(stream); var reader = pe.GetMetadataReader();
        var type = reader.TypeDefinitions.Select(reader.GetTypeDefinition).Single(item => reader.GetString(item.Name) == typeName && reader.GetString(item.Namespace) == ns);
        var field = type.GetFields().Select(reader.GetFieldDefinition).Single(item => reader.GetString(item.Name) == name);
        var value = reader.GetConstant(field.GetDefaultValue());
        if (value.TypeCode != ConstantTypeCode.String) throw new InvalidDataException("Workflow identity must remain an explicit string constant.");
        return Encoding.Unicode.GetString(reader.GetBlobBytes(value.Value));
    }
}
