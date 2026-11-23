using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;
using Cerebral.Engine.Activation;
using Cerebral.PersonalityPlugin;
using Cerebral.Tools;
using Cerebral.Worker;
using Cerebral.Contracts.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.PersonalityPlugin.Tests;
internal static partial class Program
{
    private static int WorkerPipeMain(string fixture)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources[DesktopThemeResourceKeys.TextBrush] = Brushes.Black;
        app.Resources[DesktopThemeResourceKeys.MutedTextBrush] = Brushes.DimGray;
        app.Resources[DesktopThemeResourceKeys.ErrorTextBrush] = Brushes.DarkRed;
        app.Dispatcher.BeginInvoke(new Action(async () => {
            try { await new WorkerPipeRun(fixture).RunAsync(); app.Shutdown(0); }
            catch (Exception error) { Console.Error.WriteLine("FAIL: " + error.GetType().Name + ": " + error.Message); app.Shutdown(1); }
        }));
        return app.Run();
    }
    private sealed partial class WorkerPipeRun
    {
        private const string Workspace = @"C:\Development\Pegasus-Cerebral";
        private const string Dotnet = @"C:\Program Files\dotnet\dotnet.exe";
        private const string DotnetHash = "abce6623a6729059cf39bb0f728c4a7804f625895099c88c17e6e704661e09eb";
        private const string PipeName = "pegasus.cerebral.personality.sample-source.fixture";
        private const string FirstInstance = "01a12d70255479f1958dfb1e59fda62e";
        private const string SecondInstance = "01a12d70255479f1958dfb1e59fda620";
        private const string PluginId = "pegasus.personality";
        private readonly string Fixture, Data, Project;
        private string InstallationId = "";
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private readonly CancellationTokenSource work = new(TimeSpan.FromSeconds(145));
        private readonly CancellationToken WorkToken;
        private readonly List<string> labels = [];
        private readonly List<object> launches = [];
        private readonly List<string> restoreReceipts = [];
        private readonly List<object> restoreCounterObservations = [];
        private string? wrongInstanceRefusal;
        private CerebralClient? client;
        private ManagedProcess? child;
        private LocalWorkerEndpoint? endpoint;
        private Task outPump = Task.CompletedTask, errPump = Task.CompletedTask;
        private readonly TailLog childOut = new(), childErr = new();
        private NativePipeHost? nativeHost;
        private PersonalityPlugin? nativePlugin;
        private Window? window;
        private int starts, maximumFrame, assertions;
        private bool stopAttempted;
        private bool passed, cleanupConfirmed, fixtureCreated;
        private readonly Dictionary<string,string> stagedPins = new(StringComparer.OrdinalIgnoreCase);
        private static readonly JsonSerializerOptions CompactJson = new(CerebralJson.Options) { WriteIndented = false };
        private sealed record BytePin(string Path, string Sha256);
        private static readonly BytePin[] RuntimePins = [
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\Cerebral.Client.dll", "87394e5b366281bd815f58aedfd7a7af8a4dc35666118aa2630142927b95a667"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\Cerebral.Client.pdb", "cf9daeb1ae57b7799b014091eb5648b9fddf78b70966ddbdef69b4cb466b4dd5"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\Cerebral.Contracts.dll", "c7f3b337f939cdc9b61c656947beaa5e3cceb6d0c73df029d072265595a91fa3"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\Cerebral.Contracts.pdb", "caa81664bc470dfb6969eec7c754e36bc4ba0b0b4b30ea5a174aa702968fa0ca"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\Cerebral.Conversation.dll", "55a6526f60d914ed44299c34b434f113c55f977db90bca870be4f822e4b3e171"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\Cerebral.Conversation.pdb", "466ad9b3d1aa50f1110db39f2a0ef3e6728e979f4eef7c68c8fd8556966943e0"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\Cerebral.Core.dll", "4f29f7057d1794470b1ccec9841d4484c2e6f5f3439ddeb4d0979b9d8425eb57"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\Cerebral.Core.pdb", "cefe93d3574692911a0b620cde3fd2d40a24e492cea7cf14ce7415b14fae0ca5"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\Cerebral.Engine.Contracts.dll", "d212f94bfc7b69930ccd5c33a7bf9a5fd3c3ce23f6dcd8e3072d7ebde15ef89d"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\Cerebral.Engine.Contracts.pdb", "78942f235b09ac491a1c98c1a41a8d82ddd883bfe67d354076f8aced5bd7a3ad"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\Cerebral.Engine.dll", "ead588fed7bb7ba901abdc1c86012298fab98ff20266f17ca62c31a270db84cf"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\Cerebral.Engine.pdb", "b06df2bd7f5c5a7189e9fd1b6e41a68d0bb3abe890ce15bdcdca924cc2669f9e"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\Cerebral.Tools.dll", "28436a1bda189d8c0240fdd166474e0092d2cc38e1d38536c1c445e7b142d7a6"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\Cerebral.Tools.pdb", "c005a2ba6a72135b1fcd4c27f3d44b9921d8ef5fd826bca9d93653391938106b"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\Cerebral.Worker.deps.json", "80617259c0c31f5054ff8cb7457f375c33c7490c915a4e10f5fc2f1fa02c0794"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\CompactorSchemaWorker\\Release\\net10.0\\Cerebral.Worker.dll", "b11b7c82b52552390240e05f4e343ee329549ec27ff8e4bc1eac80bf3690cd2d"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\CompactorSchemaWorker\\Release\\net10.0\\Cerebral.Worker.pdb", "2cc064baead03ec9b271b420288473e1214300040bfd01b47c9125af8c1e028a"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\Cerebral.Worker.runtimeconfig.json", "41442586a8262bc96a1c80f82f4d9384c03bfd465d221cbf01fb3563170f9e65"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\Cerebral.Worker.staticwebassets.endpoints.json", "eb6525c892afda953a2560f151f9c5e5bc6e79ef6741d886488ac450262c91e3"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\e_sqlite3.dll", "b7385d722c83fb52142a00477a726723745916d22a555711ee89834c1111fb2e"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\Microsoft.Data.Sqlite.dll", "1078d41c6c0f2cee48a8b706e3a54ce145705993f19f703d2b3bdf2abcc59088"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\Microsoft.Extensions.Hosting.Systemd.dll", "b83961fadb9b88ed05ccb4dea45165b5393b4eaf1258dce0b7d0f60e9c0c1d46"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\Microsoft.Extensions.Hosting.WindowsServices.dll", "caa306587f796d0290aada4fdfaec1394f1f206409ea4d5a20f309b61fc59959"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\SQLitePCLRaw.batteries_v2.dll", "bc69777001eb4db81c580f62835396f67e74aaa381eef446193a904499d5310b"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\SQLitePCLRaw.core.dll", "50c999a1ab2afeacc93bce5ac2948d3bb630d99d23efc3cfcbd8bc11d3426eb7"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\SQLitePCLRaw.provider.e_sqlite3.dll", "02ad639f45a26de1919ffbb6696bdb399ad01afc149970a163c3867162f310b3"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\System.ServiceProcess.ServiceController.dll", "486a4fbb24ab089926fb05ab6b95af7bb04d2ff35eef55b6858437ebc4d040dc"),
        ];
        private static readonly BytePin[] ServicePins = [
            new("build\\Plugins\\SettingsPresets\\artifacts\\bin\\Cerebral.PersonalityServicePlugin\\transfer-current\\Cerebral.PersonalityServicePlugin.dll", "135e7ae53911e1632fb0cc02599abf67b53f92e260fcc3d9b777a1fb19461005"),
            new("build\\Plugins\\SettingsPresets\\artifacts\\bin\\Cerebral.PersonalityServicePlugin\\transfer-current\\Cerebral.Core.dll", "4f29f7057d1794470b1ccec9841d4484c2e6f5f3439ddeb4d0979b9d8425eb57"),
            new("build\\Plugins\\SettingsPresets\\artifacts\\bin\\Cerebral.PersonalityServicePlugin\\transfer-current\\Cerebral.Contracts.dll", "c7f3b337f939cdc9b61c656947beaa5e3cceb6d0c73df029d072265595a91fa3"),
            new("build\\Plugins\\SettingsPresets\\artifacts\\bin\\Cerebral.PersonalityServicePlugin\\transfer-current\\service-plugin.json", "8123c76852de53194720a1c8ec4823c605b4608cc156c66b764cf02a4b5644ec"),
            new("build\\Plugins\\SettingsPresets\\artifacts\\bin\\Cerebral.PersonalityServicePlugin\\transfer-current\\Cerebral.PersonalityServicePlugin.deps.json", "3d5a9c69509178368faccc4e7efd0f055079488ced033e6f5252612c1ab47b97"),
        ];
        private static readonly BytePin[] PreservedPins = [
            new("build\\Core\\SettingsPresets\\artifacts\\bin\\Cerebral.Core\\release\\Cerebral.Core.dll", "4f29f7057d1794470b1ccec9841d4484c2e6f5f3439ddeb4d0979b9d8425eb57"),
            new("build\\Core\\SettingsPresets\\artifacts\\bin\\Cerebral.Desktop.Plugins\\release\\Cerebral.Desktop.Plugins.dll", "f8d0ac9e6060a50184585ecdccec0a8f937242e77993f86f459c001dd9f66e82"),
            new("build\\Core\\OwnerQuiescence\\artifacts\\bin\\GenericContextWorker\\Release\\net10.0\\Cerebral.Contracts.dll", "c7f3b337f939cdc9b61c656947beaa5e3cceb6d0c73df029d072265595a91fa3"),
            new("build\\Core\\SettingsPresets\\artifacts\\bin\\Cerebral.Desktop\\release\\Cerebral.Desktop.dll", "df5a35e1e7b1fd20921f1aa1f88071c88dad5ae45d88968b0520a13d96c9130c"),
            new("build\\Plugins\\SettingsPresets\\artifacts\\bin\\PersonalityEditorCandidate\\transfer\\net10.0-windows\\Cerebral.PersonalityPlugin.dll", "3796751b9cc5045d1844354d2b362f22b86e4eb491c4ab50104c030a3d667c53"),
            new("build\\\\Plugins\\\\SettingsPresets\\\\personality-editor-transfer-handoff.json", "74757563f57b6816ecaed43d2b385b4c4ab0cdca5d771f9f5af1298c913479bf"),
            new("build\\\\Plugins\\\\SettingsPresets\\\\personality-editor-transfer-verification.json", "773fd6e191f3c51d3026bbae94d533716a37152e7f628f26223a77db0bfd6a53"),
        ];
        private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) {
            "health","settings.get","agents.list","models.list","models.accounting.snapshot","metrics.slots.status","metrics.execution.status","metrics.generation",
            "service-packages.inspect","engine.status","configuration.backup.export","configuration.restore.preview","configuration.restore.apply","configuration.restore.status",
            "settings.plugins.transfer.get","personality.preferences.get","personality.preferences.save","personality.sources.get","personality.list","personality.get","personality.source.prepare",
            "personality.preferences.transfer.get","personality.preferences.transfer.save","personality.preferences.transfer.prepare","personality.preferences.pending.prepare","personality.preferences.pending.apply",
            "instructions.get","instructions.save","jobs.workflow.submit","jobs.workflow.get","jobs.workflow.receipt","jobs.workflow.events","jobs.workflow.cancel","jobs.workflow.active","service.stop-local"
        };
        internal WorkerPipeRun(string fixture)
        {
            Fixture = Path.GetFullPath(fixture);
            var exact = Path.GetFullPath(Path.Combine(Workspace, "build","Plugins","SettingsPresets","personality-fixtures","worker-working"));
            if (!Fixture.Equals(exact, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Only the reviewed fixed fixture root is admitted.");
            Data = Path.Combine(Fixture,"data"); Project = Path.Combine(Fixture,"project"); WorkToken = work.Token;
        }
        private TimeSpan Remaining(double absoluteSeconds) => TimeSpan.FromSeconds(Math.Max(0, absoluteSeconds - elapsed.Elapsed.TotalSeconds));
        private CancellationTokenSource Deadline(double absoluteSeconds, double stepSeconds, CancellationToken parent = default)
        {
            var left = Remaining(absoluteSeconds);
            if (left <= TimeSpan.Zero) throw new TimeoutException("The shared harness deadline is exhausted.");
            var token = CancellationTokenSource.CreateLinkedTokenSource(parent);
            token.CancelAfter(left < TimeSpan.FromSeconds(stepSeconds) ? left : TimeSpan.FromSeconds(stepSeconds));
            return token;
        }
        private void Assert(bool value,string label)
        {
            if (!value) throw new InvalidOperationException(label);
            assertions++; labels.Add(label); Console.WriteLine("PASS: " + label);
        }
        private static JsonElement Element(object value) => CerebralJson.Element(value);
        private static bool Same(object a,object b) => JsonElement.DeepEquals(Element(a),Element(b));
        private static string FileHash(string path)
        {
            NoLinks(path);
            using var input = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            return Convert.ToHexStringLower(SHA256.HashData(input));
        }
        private static void NoLinks(string path)
        {
            var current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A fixture or artifact path traverses a reparse point.");
                current = Path.GetDirectoryName(current)!;
            }
        }
        private static void SafeTree(string path)
        {
            NoLinks(path);
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                NoLinks(entry);
                if (Directory.Exists(entry)) SafeTree(entry);
            }
        }
        private void Stage(BytePin pin,string directory)
        {
            WorkToken.ThrowIfCancellationRequested();
            var input = Path.GetFullPath(Path.Combine(Workspace,pin.Path));
            NoLinks(input); NoLinks(directory);
            using var source = new FileStream(input,FileMode.Open,FileAccess.Read,FileShare.Read);
            if (Convert.ToHexStringLower(SHA256.HashData(source)) != pin.Sha256) throw new IOException("Pinned source bytes changed: " + Path.GetFileName(input));
            source.Position = 0;
            var destination = Path.Combine(directory,Path.GetFileName(input));
            using (var output = new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                var buffer = new byte[64 * 1024]; int count;
                while ((count = source.Read(buffer)) != 0) { WorkToken.ThrowIfCancellationRequested(); output.Write(buffer,0,count); }
                output.Flush(true);
            }
            if (FileHash(destination) != pin.Sha256) throw new IOException("Staged byte hash differs.");
            stagedPins.Add(destination,pin.Sha256);
        }
        private void VerifyPreserved()
        {
            foreach (var pin in RuntimePins.Concat(ServicePins).Concat(PreservedPins))
            {
                WorkToken.ThrowIfCancellationRequested();
                if (FileHash(Path.Combine(Workspace,pin.Path)) != pin.Sha256) throw new IOException("Accepted byte pin changed.");
            }
            Assert(FileHash(Dotnet)==DotnetHash,"reviewed exact dotnet host and accepted artifact pins match");
            Assert(FileHash(typeof(PersonalityPlugin).Assembly.Location)==PreservedPins.Single(x=>x.Path.EndsWith("Cerebral.PersonalityPlugin.dll",StringComparison.Ordinal)).Sha256,
                "runner loads the exact accepted native Personality bytes");
        }
        private void Prepare()
        {
            NoLinks(Fixture);
            if (Directory.Exists(Fixture) || File.Exists(Fixture)) throw new IOException("Fixed fixture collision; preserve it for review.");
            if (!EndpointAbsent(PipeName)) throw new IOException("The reviewed private pipe name is already present; no process is touched.");
            VerifyPreserved();
            Directory.CreateDirectory(Fixture); fixtureCreated = true;
            foreach (var name in new[]{"runtime","packages","engines-empty","data","native-data","project","models","shared","tools"}) Directory.CreateDirectory(Path.Combine(Fixture,name));
            var package = Path.Combine(Fixture,"packages",PluginId); Directory.CreateDirectory(package);
            foreach(var pin in RuntimePins) Stage(pin,Path.Combine(Fixture,"runtime"));
            foreach(var pin in ServicePins) Stage(pin,package);
            InstallationId=Guid.NewGuid().ToString("N");
            var settings = new HostSettings {
                InstallationId=InstallationId,Name="Private Personality pipe fixture",Coordinator=true,StandaloneWorker=false,
                ListenUrl="http://127.0.0.1:65534",AutoSelectAvailablePort=false,AllowLan=false,ExternalCommandsEnabled=false,
                AccessKey=Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
                ModelDirectory=Path.Combine(Fixture,"models"),ProjectsDirectory=Project,SharedDirectory=Path.Combine(Fixture,"shared"),ToolsDirectory=Path.Combine(Fixture,"tools"),
                LlamaServerPath=Path.Combine(Fixture,"absent-runtime","llama-server.exe"),DefaultModelId="",DefaultModelWorkerId="local",SharedFilesWorkerId="local",
                Workers=[],McpServers=[],Executables=[],BackgroundTasks=[],BackgroundTasksEnabled=false,CortexNamespaces=[],CortexWriteNamespaces=[],PromptCacheDiagnosticsEnabled=false
            };
            new StateStore(Data).Put("settings","host",settings);
        }
        private async Task BeginWorkerAsync()
        {
            WorkToken.ThrowIfCancellationRequested();
            if(child is not null || starts>=2)throw new InvalidOperationException("Only two sequential exact owned starts are admitted.");
            if(!EndpointAbsent(PipeName))throw new IOException("Private pipe name remains present before start.");
            if(stagedPins.Any(x=>FileHash(x.Key)!=x.Value))throw new IOException("Staged closure changed.");
            // The frozen Worker prefers an existing DOTNET_ROOT candidate. Reject another binding before launch;
            // this fixture admits only the explicitly verified standard metadata and never invokes registry entries.
            var dotnetRoot=Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if(!string.IsNullOrWhiteSpace(dotnetRoot))
            {
                var candidate=Path.GetFullPath(Path.Combine(dotnetRoot,"dotnet.exe"));
                if(File.Exists(candidate)&&!candidate.Equals(Dotnet,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Inherited DOTNET_ROOT would discover a different executable binding.");
            }
            if(UnexpectedStartupExecutablePaths.Any(File.Exists)||File.Exists(Path.Combine(Fixture,"tools","AI-Dictation","NeMo-Speech","bin","nemo-speech.exe")))
                throw new InvalidDataException("Previously absent standard executable discovery candidate is now present.");
            endpoint=new(PipeName,starts==0?FirstInstance:SecondInstance);
            child=ManagedProcess.Start(Dotnet,[Path.Combine(Fixture,"runtime","Cerebral.Worker.dll"),"--local-run","--model-admission-closed","--data",Data,
                "--pipe-name",endpoint.PipeName,"--pipe-instance",endpoint.InstanceId,"--service-plugins",Path.Combine(Fixture,"packages"),"--engines",Path.Combine(Fixture,"engines-empty")],Path.Combine(Fixture,"runtime"));
            starts++; stopAttempted=false; child.Process.StandardInput.Close();
            launches.Add(new{pid=child.Process.Id,startUtc=child.Process.StartTime.ToUniversalTime().ToString("O"),instance=endpoint.InstanceId});
            outPump=Drain(child.Process.StandardOutput,childOut);errPump=Drain(child.Process.StandardError,childErr);
            client=new CerebralClient(endpoint);
            var startupEnd=Math.Min(145,elapsed.Elapsed.TotalSeconds+30);
            while(true)
            {
                using var probe=Deadline(startupEnd,1,WorkToken);
                if(child.Process.HasExited)throw new IOException("The exact owned Worker exited before health.");
                try { var health=await client.CommandAsync("health",new{},cancellationToken:probe.Token);
                    if(health.Success)break; }
                catch(Exception error)when(error is IOException or TimeoutException or OperationCanceledException)
                { WorkToken.ThrowIfCancellationRequested(); if(Remaining(startupEnd)<=TimeSpan.Zero)throw; }
                await Task.Delay(80,WorkToken);
            }
            var info=await Read<HostInfo>("health",new{});
            Assert(info.Product==LocalWorkerProtocol.Product && info.Protocol==LocalWorkerProtocol.Version && info.LocalRun &&
                info.InstallationId==InstallationId && Path.GetFullPath(info.DataDirectory).Equals(Data,StringComparison.OrdinalIgnoreCase),
                "actual owner pipe handshake and health identify exact private Worker/data/installation");
            var engine=await Read<EngineStatus>("engine.status",new{});
            Assert(engine.Paused && engine.Active is null && engine.ActiveEvaluations==0 && engine.Slots.All(x=>!x.Available&&x.Package is null),
                "empty engine graph remains paused with no active engine or evaluations");
            var package=await Read<ServicePackageLifecycleStatus>("service-packages.inspect",new{id=PluginId});
            Assert(package.ObservedLoaded,"real private loader reports the pinned Personality service loaded");
            AssertExpectedExecutableMetadata(await Read<HostSettings>("settings.get",new{}));
            await VerifyClosedAccounting();
        }
        private sealed record StartupExecutablePin(string Id,string Path,string Sha256);
        private static readonly StartupExecutablePin[] ExpectedStartupExecutables = [
            new("dotnet",@"C:\Program Files\dotnet\dotnet.exe","abce6623a6729059cf39bb0f728c4a7804f625895099c88c17e6e704661e09eb"),
            new("powershell",@"C:\Program Files\PowerShell\7\pwsh.exe","bfb46af89433268872ddb43d1ca7a3f433452ee91ed356a9786940f90118e285"),
            new("git",@"C:\Program Files\Git\cmd\git.exe","78211c7ed73988da93a6d8a33d47ec6187f464d7ea2a9a00c182bbd7a1ecf30f"),
            new("node",@"C:\Program Files\nodejs\node.exe","ba4e6d110e8c1592a1ecd390f6b05f3da124b13871a5be62b341a07a853c6c32"),
            new("7zip",@"C:\Program Files\7-Zip\7z.exe","83967f1b02b43c4efeda302795722c809e0e81b8307de73558d10484d5676a7d"),
            new("nsis",@"C:\Program Files (x86)\NSIS\makensis.exe","b043e554afefbfc56315669d0b4779793aeae67f0f2a7a790e2ea91f05298eff")
        ];
        private static readonly string[] UnexpectedStartupExecutablePaths = [
            @"C:\Program Files\Python314\python.exe",
            @"C:\Program Files\Microsoft Visual Studio\2026\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
            @"C:\Program Files\Microsoft Visual Studio\2026\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
            @"C:\Program Files\Microsoft Visual Studio\2026\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe"
        ];
        private void AssertExpectedExecutableMetadata(HostSettings host)
        {
            Assert(host.Workers.Count==0 && host.McpServers.Count==0 && host.Executables.Count==ExpectedStartupExecutables.Length,
                "fixture has no worker/MCP/provider registrations and only the exact expected standard executable metadata count");
            for(var i=0;i<ExpectedStartupExecutables.Length;i++)
            {
                var expected=ExpectedStartupExecutables[i];var actual=host.Executables[i];
                NoLinks(expected.Path);
                Assert(actual.Id==expected.Id && actual.Path==expected.Path && !actual.RequiresConfiguration &&
                    actual.PrefixArguments.Count==0 && FileHash(expected.Path)==expected.Sha256,
                    "fixture preserves exact byte-pinned "+expected.Id+" registration metadata without registry dispatch");
            }
        }
        private async Task VerifyClosedAccounting(string phase="owned-accounting", int phaseIndex=0)
        {
            var value=await Read<JsonElement>("models.accounting.snapshot",new{schemaVersion=1,expectedWorkerInstallationId=InstallationId,expectedControllerInstallationId=InstallationId});
            ObserveEnvironment("models.accounting.snapshot",value,phase,phaseIndex);
            Assert(ClosedAccountingGuard(value,InstallationId),
                "private model admission stays closed with complete exact-identity owned accounting and no work");
        }
        private async Task<CommandResult> Call(string operation,object input,bool require=true,CancellationToken cancellation=default)
        {
            if(!Allowed.Contains(operation))throw new InvalidOperationException("Harness operation not admitted.");
            using var caller=CancellationTokenSource.CreateLinkedTokenSource(WorkToken,cancellation);
            using var deadline=Deadline(145,10,caller.Token);
            var result=await (client??throw new InvalidOperationException("Private client absent.")).CommandAsync(operation,input,profileId:null,cancellationToken:deadline.Token);
            if(result.Success)
            {
                var bytes=JsonSerializer.SerializeToUtf8Bytes(result,CompactJson).Length;maximumFrame=Math.Max(maximumFrame,bytes);
                if(bytes>LocalWorkerProtocol.MaximumFrameBytes)throw new InvalidDataException("Serialized successful command envelope exceeds the real transport frame bound.");
            }
            if(require&&!result.Success)throw new InvalidOperationException("Private command refused: "+operation+" ("+result.Error+")");
            return result;
        }
        private async Task<T> Read<T>(string operation,object input,CancellationToken cancellation=default) => CerebralJson.Read<T>((await Call(operation,input,true,cancellation)).Data);

        private async Task StopWorkerAsync(bool final,double end)
        {
            if(child is null)return;
            var owned=child;Exception? problem=null;bool normal=false,confirmed=false;
            try
            {
                if(!owned.Process.HasExited&&client is not null&&!stopAttempted)
                {
                    using var stop=Deadline(end,10);
                    stopAttempted=true;
                    try {
                        var result=await client.CommandAsync("service.stop-local",new{},cancellationToken:stop.Token);
                        if(!result.Success)problem=new IOException("Exact stop-local request was refused.");
                    }
                    catch(LocalWorkerCommandUncertainException) { /* Wait on the same owned handle; never replay stop. */ }
                    catch(Exception error)when(error is IOException or TimeoutException or OperationCanceledException){problem=error;}
                }
                if(!owned.Process.HasExited)
                {
                    using var wait=Deadline(end,30);
                    try{await owned.Process.WaitForExitAsync(wait.Token);}catch(OperationCanceledException){}
                }
                normal=owned.Process.HasExited&&owned.Process.ExitCode==0;
            }
            catch(Exception error)when(error is IOException or TimeoutException or OperationCanceledException){problem=error;}
            finally
            {
                var remaining=Remaining(end);
                if(remaining>TimeSpan.Zero)
                    confirmed=await owned.StopAndConfirmAsync(remaining<TimeSpan.FromSeconds(30)?remaining:TimeSpan.FromSeconds(30));
                // On the first stop retain an unconfirmed job for the outer shared cleanup budget.
                if(confirmed||final)
                {
                    if(!confirmed){try{owned.Stop();}catch(Exception error)when(error is not OutOfMemoryException){problem??=error;}}
                    client?.Dispose();client=null;
                    try {
                        if(Remaining(end)>TimeSpan.Zero){using var pumps=Deadline(end,2);await Task.WhenAll(outPump,errPump).WaitAsync(pumps.Token);}
                    }
                    catch(Exception error)when(error is IOException or OperationCanceledException or TimeoutException){problem??=error;}
                    try{owned.Dispose();}catch(Exception error)when(error is not OutOfMemoryException){problem??=error;}
                    child=null;
                }
            }
            if(!confirmed)throw new IOException("Exact owned job closure remains unconfirmed within shared remaining budget.",problem);
            if(final)cleanupConfirmed=true;
            Assert(normal,"exact private Worker exited normally after one stop-local request");
            Assert(EndpointAbsent(PipeName),"exact owned pipe endpoint is absent after job closure");
            SqliteConnection.ClearAllPools();
            var lockPath=Path.Combine(Data,"service.lock");
            if(File.Exists(lockPath)){using var handle=new FileStream(lockPath,FileMode.Open,FileAccess.ReadWrite,FileShare.None);}
            Assert(true,"private service lock is absent or can be reopened exclusively after owned job closure");
            if(problem is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(problem).Throw();
        }
        private async Task CleanupAsync(double end)
        {
            Exception? problem=null;
            try{window?.Close();window=null;nativePlugin?.Dispose();nativePlugin=null;}
            catch(Exception error)when(error is not OutOfMemoryException){problem=error;}
            if(nativeHost is not null)
            {
                try{nativeHost.Release.TrySetResult();nativeHost.Dispose();}catch(Exception error)when(error is not OutOfMemoryException){problem??=error;}
                try{using var wait=Deadline(end,2);await nativeHost.CompleteAsync(wait.Token);}
                catch(Exception error)when(error is not OutOfMemoryException){problem??=error;}
                nativeHost=null;
            }
            if(client is not null)
                foreach(var id in ContextRunIds)
                {
                    try
                    {
                        using var cancel=Deadline(end,10);
                        var response=await client.CommandAsync("jobs.workflow.cancel",new{id},cancellationToken:cancel.Token);
                        if(!response.Success)throw new IOException("Exact context run cancellation was not acknowledged.");
                        var run=CerebralJson.Read<WorkflowRun>(response.Data);
                        var result=await client.CommandAsync("jobs.workflow.get",new{id},cancellationToken:cancel.Token);
                        if(!result.Success)throw new IOException("Exact cancelled context run could not be read back.");
                        var readback=CerebralJson.Read<WorkflowRun>(result.Data);
                        if(run.Id!=id||run.Status!=WorkflowStatus.Cancelled||!run.CancelRequested||run.Effect is not null||run.Steps!=0||
                           readback.Id!=id||readback.Status!=WorkflowStatus.Cancelled||!readback.CancelRequested||readback.Steps!=0||readback.Effect is not null||readback.Revision!=run.Revision)
                            throw new IOException("Context run cancellation is not terminal with absent effects.");
                    }
                    catch(Exception error)when(error is not OutOfMemoryException){problem??=error;}
                }
            try{await StopWorkerAsync(true,end);}
            catch(Exception error)when(error is not OutOfMemoryException){problem??=error;}
            if(child is null && problem is null)cleanupConfirmed=true;
            if(problem is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(problem).Throw();
        }
        internal async Task RunAsync()
        {
            Exception? failure=null;
            try
            {
                Prepare();await BeginWorkerAsync();await Phase1Async();
                await StopWorkerAsync(false,Math.Min(145,elapsed.Elapsed.TotalSeconds+30));
                await RunContextPhaseAsync();await VerifyClosedAccounting();
                VerifyPreserved();passed=true;
            }
            catch(Exception error){failure=error;Console.Error.WriteLine("FUNCTIONAL: "+error.GetType().Name+": "+error.Message);}
            finally
            {
                var cleanupEnd=Math.Min(180,elapsed.Elapsed.TotalSeconds+35);
                try {await CleanupAsync(cleanupEnd);}catch(Exception error){failure??=error;cleanupConfirmed=false;}
                try
                {
                    if(fixtureCreated && passed && cleanupConfirmed && failure is null)
                    {
                        if(stagedPins.Any(x=>FileHash(x.Key)!=x.Value))throw new IOException("Staged graph changed before cleanup.");
                        SafeTree(Fixture);if(!Fixture.Equals(Path.Combine(Workspace,"build","Plugins","SettingsPresets","personality-fixtures","worker-working"),StringComparison.OrdinalIgnoreCase))throw new IOException("Cleanup target changed.");
                        if(Remaining(cleanupEnd)<=TimeSpan.Zero)throw new TimeoutException("Cleanup shared budget exhausted before removal.");
                        Directory.Delete(Fixture,true);
                    }
                    if(Remaining(cleanupEnd)<=TimeSpan.Zero)throw new TimeoutException("Cleanup shared budget exhausted before report.");
                    using var reportToken=Deadline(cleanupEnd,Remaining(cleanupEnd).TotalSeconds);
                    var report=new {schemaVersion=1,taskId="10000000-0000-4000-8000-000000000002",status=passed&&cleanupConfirmed&&failure is null?"passed-private-fixture":"failed-or-cleanup-unconfirmed",
                        assertions,labels,launches,elapsedSeconds=elapsed.Elapsed.TotalSeconds,maximumSerializedCommandResultBytes=maximumFrame,cleanupConfirmed,fixtureRemoved=!Directory.Exists(Fixture),
                        acceptedRuntimePins=RuntimePins,servicePins=ServicePins,nativePins=PreservedPins,restoreReceipts,restoreCounterObservations,wrongInstanceRefusal,environmentObservations,
                        contextFixture=new{profileId="personality-fixture-lead",modelId="personality-fixture-unregistered-model",ContextProfileRevision,ContextProfileUtf8Sha256,ContextProfileUtf8Bytes,runIds=ContextRunIds.ToArray(),
                            plannedSeedKind="constructed-stopped-database",phaseReached=ContextPhaseReached,seedReached=ContextSeedReached,
                            seedPerformed=ContextSeedVerified,seedWriteStage=ContextSeedWriteStage,
                            seedWritesPerformed=ContextSeedWriteStage is "host-and-profile-written" or "verified"},
                        failure=failure is null?null:new{type=failure.GetType().Name,message=failure.Message},
                        childOutput=new{stdoutTotalCharacters=childOut.Length,stderrTotalCharacters=childErr.Length,
                            stdoutTailUtf8Sha256=childOut.Digest(),stderrTailUtf8Sha256=childErr.Digest(),tailMaximumCharacters=4096,
                            hashesCoverBoundedTailOnly=true,aggregatesBothOwnedStarts=true},
                        deadline=new{workEndSeconds=145,totalCooperativeEndSeconds=180,sharedCleanupSeconds=35,independentOuterSupervisorVerified=false,
                            limitation="Synchronous hashing/tree traversal/deletion cannot be interrupted by cancellation; no hard wall-clock-bound proof."},
                        startupExecutables=ExpectedStartupExecutables.Select(x=>new{id=x.Id,path=x.Path,sha256=x.Sha256,prefixArguments=Array.Empty<string>(),requiresConfiguration=false,metadataOnly=true}).ToArray(),
                        limits=new[]{"Native host/binding and acknowledgement-loss/delay are constructed frontend fixtures; no actual Desktop catalog lifecycle.","Phase2 lead/default binding is constructed private stopped-DB setup, not public provisioning.","No provider/model/GPU/voice/network/installed/Source, populated user-data sanitization or external legacy writer retirement."}};
                    var reportPath=Path.Combine(Workspace,"build","Plugins","SettingsPresets","personality-worker-pipe-verification.json");
                    var logPath=Path.Combine(Workspace,"build","Plugins","SettingsPresets","personality-worker-pipe-tests.log");
                    var temporary=reportPath+".part";
                    NoLinks(reportPath);NoLinks(logPath);NoLinks(temporary);
                    if(File.Exists(temporary)||Directory.Exists(temporary))throw new IOException("Owned report staging collision; preserve it.");
                    // Required log is committed first. Only a fully written report is published as the final outcome.
                    await File.WriteAllTextAsync(logPath,string.Join("\n",labels),reportToken.Token);
                    try
                    {
                        await File.WriteAllTextAsync(temporary,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}),reportToken.Token);
                        reportToken.Token.ThrowIfCancellationRequested();NoLinks(reportPath);NoLinks(temporary);
                        File.Move(temporary,reportPath,true);
                    }
                    finally{if(File.Exists(temporary)){NoLinks(temporary);File.Delete(temporary);}}
                }
                catch(Exception error){failure??=error;}
                work.Dispose();
            }
            if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            Console.WriteLine("PASS: actual private Worker/pipe harness assertions="+assertions+"; no model/provider/Source.");
        }

        private async Task<PluginSettingsTransferState> Transfer(CancellationToken cancellation=default) => await Read<PluginSettingsTransferState>("personality.preferences.transfer.get",new{},cancellation);
        private void ObserveRestoreCounters(string phase,string previewId,int expectedPending,SettingsRestoreSummary? summary)
        {
            // Eight producers have one preview, Apply and receipt observation each. Never store backup bodies or credentials.
            if(restoreCounterObservations.Count>=24)throw new InvalidOperationException("Bounded restore counter observations are full.");
            restoreCounterObservations.Add(new{phase,previewId,expectedTotalPending=expectedPending,
                summaryAvailable=summary is not null,appliedPluginDocuments=summary?.AppliedPluginDocuments,pendingPluginDocuments=summary?.PendingPluginDocuments});
        }
        private async Task<PluginSettingsTransferState> ProducePending(bool portable,JsonElement values)
        {
            var current=await Transfer();
            AssertExpectedExecutableMetadata(await Read<HostSettings>("settings.get",new{}));
            var exported=await Read<ConfigurationBackupExport>("configuration.backup.export",new{});
            Assert(exported.Document.Agents.Count==0 && exported.Document.Models.Count==0 &&
                exported.Document.Connections is {Workers.Count:0,McpServers.Count:0} connections &&
                connections.Executables.SequenceEqual(ExpectedStartupExecutables.Select(x=>new ConfigurationBackupExecutable(x.Id,x.Path,false))),
                "producer backup has no profiles/models/provider connections and preserves exact expected executable metadata projections");
            Assert(current.State.Coordinate==new PluginSettingsCoordinate(PluginId,"global",null) &&
                exported.Document.Settings is {PluginDocuments.Count:>0} backupSettings &&
                backupSettings.PluginDocuments.All(x=>x.Document.Coordinate==current.State.Coordinate),
                "producer aggregate-count fixture contains only the exact global Personality coordinate");
            var expectedPending=checked(current.State.Pending.Count+1);
            var incoming=new PluginSettingsDocument(PluginId,"global",null,"1.2.1",2,values);
            var document=exported.Document with{
                SchemaVersion=portable?14:15,SettingsTransferMode=portable?null:SettingsTransferPurpose.Complete,
                Agents=[],Models=[],Prompts=[],Skills=[],
                Settings=new ConfigurationBackupSettings(
                    [new ConfigurationBackupSettingsEntry("pending",incoming,Guid.NewGuid().ToString("N"))],
                    [],exported.Document.Settings?.AppearancePreset??Element(new{}))};
            var preview=await Read<ConfigurationRestorePreview>("configuration.restore.preview",new ConfigurationRestorePreviewRequest(document));
            ObserveRestoreCounters("preview",preview.PreviewId,expectedPending,preview.Settings);
            Assert(preview.Settings is {AppliedPluginDocuments:0} && preview.Settings.PendingPluginDocuments==expectedPending,
                "actual restore producer preview preserves retained inputs and reports the exact planned total");
            var apply=await Read<ConfigurationRestoreResult>("configuration.restore.apply",new ConfigurationRestoreApplyRequest(preview.PreviewId,preview.ExpectedHostRevision,true));
            ObserveRestoreCounters("apply",preview.PreviewId,expectedPending,apply.Settings);
            var receipt=await Read<ConfigurationRestoreStatus>("configuration.restore.status",new ConfigurationRestoreStatusRequest(preview.PreviewId));
            ObserveRestoreCounters("receipt",preview.PreviewId,expectedPending,receipt.Receipt?.Result.Settings);
            Assert(apply.AgentIds.Count==0 && apply.ModelIds.Count==0 && apply.Settings is {AppliedPluginDocuments:0} &&
                apply.Settings.PendingPluginDocuments==expectedPending && receipt.State=="committed" && receipt.Receipt is not null &&
                receipt.Receipt.Result.Settings is {AppliedPluginDocuments:0} && receipt.Receipt.Result.Settings.PendingPluginDocuments==expectedPending &&
                Same(receipt.Receipt.Result,apply),"actual pending restore commits exact total and receipt without model/agent provisioning");
            restoreReceipts.Add(preview.PreviewId);
            AssertExpectedExecutableMetadata(await Read<HostSettings>("settings.get",new{}));
            var after=await Transfer();
            Assert(Same(current.State.Active!,after.State.Active!) && after.State.Pending.Count==expectedPending &&
                Same(current.State.Pending,after.State.Pending.Take(current.State.Pending.Count).ToArray()) &&
                Same(current.PendingPurposes,after.PendingPurposes.Take(current.PendingPurposes.Count).ToArray()),
                "actual pending producer preserves current full active document and every retained original/purpose while appending one input");
            var added=after.State.Pending.Last();
            Assert(Same(added.Document,incoming) && after.PendingPurposes.Last()==new PluginSettingsPendingPurpose(added.Id,
                portable?SettingsTransferPurpose.Portable:SettingsTransferPurpose.Complete),
                "new retained original matches exact submitted document and authoritative purpose");
            return after;
        }
        private static bool SameRetained(PluginSettingsTransferState before,PluginSettingsTransferState after,string? removed=null) =>
            Same(before.State.Pending.Where(x=>x.Id!=removed).ToArray(),after.State.Pending) &&
            Same(before.PendingPurposes.Where(x=>x.PendingId!=removed).ToArray(),after.PendingPurposes);
        private async Task Phase1Async()
        {
            var first=await Transfer();
            var values=Element(new{enabled=true,selectedPresetId="style-fixture",
                customPresets=new[]{new{id="style-fixture",name="Fixture current style",text="Use concise practical explanations and distinguish verified evidence."}}});
            await Read<PluginSettingsSaveResult>("personality.preferences.save",new{schemaVersion=2,expectedRevision=first.State.Revision,values});
            var active=await Transfer();Assert(active.State.Active is not null && JsonElement.DeepEquals(active.State.Active.Values,values),"normal actual service save admits enabled definition without source preparation");
            var initialSources=await Read<JsonElement>("personality.sources.get",new{});
            var missing=await Call("personality.get",new{id="style-fixture"},false);
            Assert(!missing.Success,"current enabled custom definition begins with no available immutable source");
            var localStore=new PersonalitySettingsStore(Path.Combine(Fixture,"native-data"));
            var old=new PersonalityPreset("previous","Previous fixture style","Use a marked previous style.");
            localStore.Save(new(true,old.Id,[old]),localStore.Load().Revision);
            var localBytes=File.ReadAllBytes(localStore.Path);
            var lead=await Read<SystemInstructionDocument>("instructions.get",new{role="lead"});
            var originalLead=lead.Content;
            var marked=PersonalityInstructionLayer.Apply(originalLead,old,true);
            await Read<SystemInstructionDocument>("instructions.save",new{role="lead",content=marked,expectedSha256=lead.Sha256});
            await ProducePending(true,Element(new{}));
            // Core deduplicates equal documents by purpose. Distinct unselected definitions retain seven complete originals
            // without changing the selected source used by cleanup, portable Apply or the later context capture.
            for(var i=0;i<7;i++)
            {
                var incoming=i==0?values:Element(new{enabled=true,selectedPresetId="style-fixture",
                    customPresets=values.GetProperty("customPresets").EnumerateArray().Select(x=>x.Clone())
                        .Append(Element(new{id="unselected-fixture-"+i,name="Unselected fixture "+i,text="Distinct retained complete input "+i+"."})).ToArray()});
                await ProducePending(false,incoming);
            }
            var retained=await Transfer();
            Assert(retained.State.Pending.Count==8 && retained.PendingPurposes.Count==8 &&
                retained.PendingPurposes.Count(x=>x.Purpose==SettingsTransferPurpose.Portable)==1 &&
                retained.PendingPurposes.Count(x=>x.Purpose==SettingsTransferPurpose.Complete)==7,
                "real producer retains eight original inputs with authoritative one-to-one purposes");
            var frameBytes=JsonSerializer.SerializeToUtf8Bytes(CommandResult.Ok(retained),CompactJson).Length;
            Assert(frameBytes<LocalWorkerProtocol.MaximumFrameBytes,"eight-input serialized command result fits real 4MiB frame without truncation");
            var normal=await Call("personality.preferences.get",new{},false);
            Assert(!normal.Success,"normal preference getter retains complete-pending refusal over actual pipe");
            var ordinaryPrep=await Call("personality.source.prepare",new{schemaVersion=1,expectedSettingsRevision=retained.State.Revision,
                expectedStoreRevision=initialSources.GetProperty("revision").GetInt64(),expectedStoreSha256=initialSources.GetProperty("sha256").GetString()},false);
            Assert(!ordinaryPrep.Success,"normal source preparation cannot bypass complete-pending refusal");
            nativeHost=new NativePipeHost(this,Path.Combine(Fixture,"native-data"));
            nativePlugin=new PersonalityPlugin();nativePlugin.Initialize(nativeHost);
            var view=nativePlugin.ConfigurationPanels.Single().CreateView();((ScrollViewer)view).Background=Brushes.White;
            T Control<T>(string id)where T:FrameworkElement=>(T)Program.Nodes(view).OfType<FrameworkElement>().Single(x=>AutomationProperties.GetAutomationId(x)==id);
            var choices=Control<ComboBox>("PersonalityPending");var prepare=Control<Button>("PersonalityPendingPrepare");
            var cleanup=Control<Button>("PersonalityPendingCleanup");var applyButton=Control<Button>("PersonalityPendingApply");
            var reload=Control<Button>("PersonalityPendingReload");var status=Control<TextBlock>("PersonalityPendingStatus");
            window=new Window{Title="Private Personality owner-pipe frontend fixture",Content=view,Width=820,Height=900};
            var visibleEnd=Math.Min(145,elapsed.Elapsed.TotalSeconds+20);
            using var visible=Deadline(visibleEnd,20,WorkToken);
            using var close=visible.Token.Register(()=>Application.Current.Dispatcher.BeginInvoke(new Action(()=>window?.Close())));
            async Task Until(Func<bool> predicate)
            {
                while(!predicate()){visible.Token.ThrowIfCancellationRequested();await Task.Delay(20,visible.Token);}
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
            window.Show();await Until(()=>choices.Items.Count==8&&prepare.IsEnabled);
            Assert(window.IsVisible&&!Control<Button>("PersonalitySave").IsEnabled,"shown software-WPF frontend loads real retained inputs while normal editor refuses complete pending");
            var portable=retained.PendingPurposes.Single(x=>x.Purpose==SettingsTransferPurpose.Portable).PendingId;
            choices.SelectedValue=portable;prepare.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(()=>reload.IsEnabled&&!prepare.IsEnabled);
            Assert(status.Text.Contains("Remove the previous style section",StringComparison.Ordinal),"actual pipe Review refuses unavailable enabled source beside recognized legacy section");
            var sourcesAfterRefusal=await Read<JsonElement>("personality.sources.get",new{});
            Assert(Same(initialSources,sourcesAfterRefusal)&&SameRetained(retained,await Transfer())&&File.ReadAllBytes(localStore.Path).SequenceEqual(localBytes),
                "Review refusal preserves source-store bytes, every retained original/purpose and local legacy bytes");
            reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>cleanup.IsEnabled);
            cleanup.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>prepare.IsEnabled&&!cleanup.IsEnabled);
            var restored=await Transfer();var cleanLead=await Read<SystemInstructionDocument>("instructions.get",new{role="lead"});
            Assert(JsonElement.DeepEquals(restored.State.Active!.Values,values)&&SameRetained(retained,restored)&&cleanLead.Content==originalLead&&
                File.ReadAllBytes(localStore.Path).SequenceEqual(localBytes),"real owner cleanup restores full enabled destination and preserves pending originals/purposes/surrounding lead/local bytes");
            Assert(nativeHost.SourcePrepareObservedDisabled&&nativeHost.InstructionsSaveObservedDisabled,"actual backend readback before native source/instruction dispatch observes desired preference disabled");
            choices.SelectedValue=portable;prepare.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>applyButton.IsEnabled);
            Assert(JsonElement.DeepEquals(nativeHost.LastPreparation.GetProperty("values"),values)&&
                Control<TextBox>("PersonalityPendingReview").Text.Contains("Style enabled: Yes",StringComparison.Ordinal),
                "fresh shown selected review matches original full enabled preferences and package-produced pins");
            nativeHost.LoseNextApplyAcknowledgement=true;
            applyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>reload.IsEnabled&&!applyButton.IsEnabled&&!prepare.IsEnabled);
            Assert(status.Text.Contains("do not replay",StringComparison.Ordinal)&&nativeHost.SuccessfulApplyAcknowledgementLost,
                "frontend-injected acknowledgement loss follows a successful real Apply and disables replay");
            var applied=await Transfer();
            Assert(JsonElement.DeepEquals(applied.State.Active!.Values,values)&&SameRetained(retained,applied,portable),
                "exact real readback after lost frontend acknowledgement proves only selected portable consumed and original enabled destination retained");
            var count=nativeHost.CommandCount;applyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(nativeHost.CommandCount==count,"consumed shown Apply cannot dispatch again");
            reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>choices.Items.Count==7&&prepare.IsEnabled);
            Assert(!applyButton.IsEnabled,"shown actual-state reconciliation requires a new selected review");
            nativeHost.HoldAfter="personality.preferences.transfer.get";
            reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await nativeHost.Held.Task.WaitAsync(visible.Token);
            window.Close();window=null;nativePlugin.Dispose();nativePlugin=null;
            nativeHost.Release.TrySetResult();await nativeHost.CompleteAsync(visible.Token);
            Assert(!prepare.IsEnabled&&!applyButton.IsEnabled&&!reload.IsEnabled&&nativeHost.HeldTokenCancelled,
                "unload/dispose rejects a frontend-delayed completed pipe read; cancellation and late visual update are bounded");
            nativeHost.Dispose();nativeHost=null;
            // Each remaining complete original has its own explicit policy/source preparation and selected-only acknowledgement.
            while((await Transfer()).State.Pending.Count>0)
            {
                var before=await Transfer();var selected=before.State.Pending[0];
                var purpose=before.PendingPurposes.Single(x=>x.PendingId==selected.Id).Purpose;
                var source=await Read<JsonElement>("personality.sources.get",new{});
                var prepared=await Read<JsonElement>("personality.preferences.pending.prepare",new{schemaVersion=1,expectedRevision=before.State.Revision,pendingId=selected.Id,expectedPurpose=purpose,
                    expectedStoreRevision=source.GetProperty("revision").GetInt64(),expectedStoreSha256=source.GetProperty("sha256").GetString()});
                var preparedValues=prepared.GetProperty("values");
                Assert(purpose==SettingsTransferPurpose.Complete && JsonElement.DeepEquals(preparedValues,selected.Document.Values),
                    "deliberate complete preparation admits its exact distinct original full values");
                var preset=prepared.GetProperty("preset");
                await Read<PluginSettingsTransferSaveResult>("personality.preferences.pending.apply",new{schemaVersion=1,expectedRevision=before.State.Revision,pendingId=selected.Id,expectedPurpose=purpose,
                    pendingSha256=prepared.GetProperty("pendingSha256").GetString(),valuesSha256=prepared.GetProperty("valuesSha256").GetString(),
                    expectedSourceId=preset.GetProperty("sourceId").GetString(),expectedSourceRevision=preset.GetProperty("revision").GetInt64(),expectedSourceSha256=preset.GetProperty("sha256").GetString()});
                var after=await Transfer();
                Assert(JsonElement.DeepEquals(after.State.Active!.Values,preparedValues)&&SameRetained(before,after,selected.Id),"deliberate remaining complete admission installs its prepared full values and removes only its exact selected input/purpose");
            }
            Assert((await Call("personality.preferences.get",new{})).Success,"normal getter resumes only after all remaining complete inputs explicitly admitted");
            var beforeStale=await Transfer();
            var rejected=await Call("personality.preferences.transfer.save",new{schemaVersion=2,expectedRevision=beforeStale.State.Revision-1,values},false);
            Assert(!rejected.Success&&Same(beforeStale,await Transfer()),"stale central CAS refuses without changing preferences or pending");
            using(var wrong=new CerebralClient(new LocalWorkerEndpoint(PipeName,Guid.NewGuid().ToString("N"))))
            {
                var refused=false;try{using var wrongDeadline=Deadline(145,10,WorkToken);await wrong.CommandAsync("health",new{},cancellationToken:wrongDeadline.Token);}
                // A rejected hello may close before its reply header; other transport failures still fail this probe.
                catch(Exception error) when(error is InvalidDataException or EndOfStreamException)
                {refused=true;wrongInstanceRefusal=error.GetType().Name;}
                Assert(refused,"wrong private pipe instance health handshake refused with "+wrongInstanceRefusal);
            }
            await VerifyClosedAccounting();
        }
        private sealed class NativePipeHost(WorkerPipeRun owner,string data):IDesktopPluginHost,IDesktopServiceHost,IDesktopPluginEnvironment,IDisposable
        {
            private readonly CancellationTokenSource lifetime=CancellationTokenSource.CreateLinkedTokenSource(owner.WorkToken);
            private readonly List<Task> pending=[];private bool disposed;
            internal int CommandCount;internal bool LoseNextApplyAcknowledgement,SuccessfulApplyAcknowledgementLost,SourcePrepareObservedDisabled,InstructionsSaveObservedDisabled,HeldTokenCancelled;
            internal string? HoldAfter;internal JsonElement LastPreparation;
            internal readonly TaskCompletionSource Held=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
            public string DataDirectory=>data;public string PackageDirectory=>AppContext.BaseDirectory;public DesktopPluginSurface Surface=>DesktopPluginSurface.Configuration;
            public DesktopConversationSnapshot GetConversation()=>new(new("private-personality-owner-pipe-frontend",null),"Fixture",null,null,false);
            public DesktopDraftSnapshot GetDraft()=>new(GetConversation().Binding,0,"");
            public event EventHandler<DesktopConversationChangedEventArgs>? ConversationChanged{add{}remove{}}
            public event EventHandler<DesktopLeadMessageEventArgs>? LeadMessageCommitted{add{}remove{}}
            public Task<DesktopServiceCommandResult> ExecuteServiceCommandAsync(string operation,object arguments,CancellationToken token=default)
            {
                ObjectDisposedException.ThrowIf(disposed,this);
                var task=Execute(operation,arguments,token);pending.Add(task);return task;
            }
            private async Task<DesktopServiceCommandResult> Execute(string operation,object arguments,CancellationToken token)
            {
                using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);
                linked.Token.ThrowIfCancellationRequested();CommandCount++;
                if(operation is "personality.preferences.transfer.prepare" or "instructions.save")
                {
                    var state=await owner.Transfer(linked.Token);
                    var disabled=state.State.Active is { } active&&!active.Values.GetProperty("enabled").GetBoolean();
                    if(operation=="personality.preferences.transfer.prepare")SourcePrepareObservedDisabled=disabled;
                    else InstructionsSaveObservedDisabled=disabled;
                }
                var result=await owner.Call(operation,arguments,false,linked.Token);
                if(operation=="personality.preferences.pending.prepare"&&result.Success)LastPreparation=result.Data.Clone();
                if(operation=="personality.preferences.pending.apply"&&result.Success&&LoseNextApplyAcknowledgement)
                {
                    LoseNextApplyAcknowledgement=false;SuccessfulApplyAcknowledgementLost=true;
                    throw new IOException("Frontend injected acknowledgement loss after successful real owner-pipe Apply.");
                }
                if(operation==HoldAfter)
                {
                    HoldAfter=null;Held.TrySetResult();
                    // Deliberately held frontend acknowledgement after a completed real pipe read; not a simulated server/pipe failure.
                    await Release.Task;HeldTokenCancelled=linked.IsCancellationRequested;
                }
                linked.Token.ThrowIfCancellationRequested();
                return new(result.Success,result.Data,result.Error);
            }
            internal async Task CompleteAsync(CancellationToken token)
            {
                try{await Task.WhenAll(pending.ToArray()).WaitAsync(token);}
                catch(Exception error)when(error is IOException or OperationCanceledException or InvalidOperationException)
                {if(pending.Any(x=>!x.IsCompleted))throw;}
            }
            public Task<DesktopOperationResult> ReplaceDraftAsync(DesktopDraftUpdate value,CancellationToken token=default)=>throw new NotSupportedException();
            public Task<DesktopOperationResult> SubmitAsync(DesktopSubmission value,CancellationToken token=default)=>throw new NotSupportedException();
            public Task<DesktopOperationResult> SteerAsync(DesktopSubmission value,CancellationToken token=default)=>throw new NotSupportedException();
            public Task<DesktopOperationResult> StopAsync(DesktopConversationBinding value,CancellationToken token=default)=>throw new NotSupportedException();
            public void DismissInputGuidance(){}public void ReportStatus(string text){}public void RefreshPresentation(){}
            public void Dispose(){if(disposed)return;disposed=true;lifetime.Cancel();lifetime.Dispose();}
        }

        private static async Task Drain(StreamReader input,TailLog log)
        {var buffer=new char[1024];int count;while((count=await input.ReadAsync(buffer))!=0)log.Add(buffer.AsSpan(0,count));}
        private sealed class TailLog
        {
            private readonly StringBuilder tail=new();internal int Length{get;private set;}
            internal void Add(ReadOnlySpan<char> value){lock(tail){Length+=value.Length;tail.Append(value);if(tail.Length>4096)tail.Remove(0,tail.Length-4096);}}
            internal string Digest(){lock(tail)return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tail.ToString())));}
        }
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]private static extern bool WaitNamedPipe(string name,uint timeout);
        private static bool EndpointAbsent(string name)
        {
            if(WaitNamedPipe(@"\\.\pipe\"+name,0))return false;
            return Marshal.GetLastPInvokeError()==2;
        }
    }
}
