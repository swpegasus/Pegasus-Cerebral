using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;
using Cerebral.Tools;

internal static class ScopedPathAccessTests
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        void Reject(Action action, string label)
        {
            try { action(); } catch (Exception e) when (e is UnauthorizedAccessException or ArgumentException) { Check(true, label); return; }
            throw new InvalidOperationException(label);
        }
        var temporaryParent = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(temporaryParent, "cerebral-path-access-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = Path.Combine(root, "project");
            var external = Path.Combine(root, "references");
            var nested = Path.Combine(project, "read-only");
            Directory.CreateDirectory(project); Directory.CreateDirectory(external); Directory.CreateDirectory(nested);
            var source = Path.Combine(external, "reference.txt");
            await File.WriteAllTextAsync(source, "reference café");
            var readOnlyScript = Path.Combine(external, "inspect.ps1");
            await File.WriteAllTextAsync(readOnlyScript, "Write-Output 'fixture'");
            var readOnlyProject = Path.Combine(project, "compilation.csproj");
            var externalProject = Path.Combine(external, "external.csproj");
            var deniedProject = Path.Combine(root, "denied.csproj");
            await File.WriteAllTextAsync(readOnlyProject, "<Project />");
            await File.WriteAllTextAsync(externalProject, "<Project />");
            await File.WriteAllTextAsync(deniedProject, "<Project />");
            var restricted = Path.Combine(nested, "kept.txt");
            await File.WriteAllTextAsync(restricted, "unchanged");
            var scope = ScopedPathAccessPolicy.Normalize(new(1,
                [new(project, ScopedPathAccess.Write), new(external, ScopedPathAccess.Read),
                    new(nested, ScopedPathAccess.Read), new(readOnlyProject, ScopedPathAccess.Read)]));
            var profile = new AgentProfile { AllowedRoots = [project], AllowedTools = ["*"],
                Capabilities = ["filesystem.read", "filesystem.write", "process.execute", "shared.read", "shared.write", "git.read"] };
            var context = new ToolExecutionContext(profile, "paths", "paths", project) { PathAccess = scope, EnforceProfileScope = true };
            Check(ScopedFilePolicy.PathInScope(source, context, ScopedPathAccess.Read, true) == source, "Explicit external read is independent of reusable profile roots.");
            Reject(() => ScopedFilePolicy.PathInScope(source, context), "Original overload conservatively requires Write for scoped old providers.");
            Reject(() => ScopedFilePolicy.PathInScope(source, context with { FullAccess = true }), "Application Full Access cannot bypass a present RO ceiling.");
            Reject(() => ScopedFilePolicy.PathInScope(restricted, context), "A more-specific RO rule overrides an RW ancestor.");
            Check(ScopedFilePolicy.PathInScope(Path.Combine(project, "new.txt"), context) == Path.Combine(project, "new.txt"), "Unrestricted siblings retain project RW permission.");
            Reject(() => ScopedFilePolicy.PathInScope(Path.Combine(root, "references-other", "x"), context, ScopedPathAccess.Read), "Grant prefixes require a directory boundary.");
            Reject(() => ScopedFilePolicy.PathInScope(Path.Combine(external, "..", "project", "x"), context, ScopedPathAccess.Read), "Parent traversal is still rejected.");
            Reject(() => ScopedFilePolicy.PathInScope(source, context with { PathAccess = null }, ScopedPathAccess.Read), "Legacy null scope retains profile confinement.");
            var read = await ScopedFilePolicy.ReadTextAsync(source, context, 4096, default);
            Check(read.Text == "reference café", "Shared semantic text reader supports RO source references.");
            var exactOverride = context with { PathAccess = new(1, [new(external, ScopedPathAccess.Read), new(source, ScopedPathAccess.Write)]) };
            Check(ScopedFilePolicy.PathInScope(source, exactOverride) == source, "Explicit more-specific RW entry is effective for that exact path.");
            Reject(() => ScopedFilePolicy.PathInScope(Path.Combine(external, "sibling.txt"), exactOverride), "Exact file RW does not authorize a sibling.");
            Reject(() => ScopedPathAccessPolicy.Normalize(new(2, [])), "Unsupported scope version rejects.");
            Reject(() => ScopedPathAccessPolicy.Normalize(new(1, null!)), "Missing grant array rejects.");
            Reject(() => ScopedPathAccessPolicy.Normalize(new(1, [new("relative", ScopedPathAccess.Read)])), "Relative grant rejects.");
            Reject(() => ScopedPathAccessPolicy.Normalize(new(1, [new(project, (ScopedPathAccess)10)])), "Unknown access enum rejects.");
            Reject(() => ScopedPathAccessPolicy.Normalize(new(1, [new(project, ScopedPathAccess.Read), new(project + Path.DirectorySeparatorChar, ScopedPathAccess.Write)])), "Duplicate canonical grant rejects.");
            Reject(() => ScopedPathAccessPolicy.Normalize(new(1, Enumerable.Range(0,129).Select(i => new ScopedPathGrant(Path.Combine(root,i.ToString()),ScopedPathAccess.Read)).ToArray())), "Scope allocation bound rejects before path work.");
            Reject(() => ScopedFilePolicy.PathInScope(source, context with { PathAccess = new(1,[]) }, ScopedPathAccess.Read), "An explicit empty scope grants no file access.");

            var settings = new HostSettings { SharedDirectory = external, SharedFilesWorkerId = "local",
                ExternalCommandsEnabled = true, Executables = [new() { Id = "fixture", Path = Environment.ProcessPath! },
                    new() { Id = "dotnet", Path = Environment.ProcessPath! }] };
            await using var dispatcher = new ToolDispatcher(() => settings, stateDirectory: Path.Combine(root,"state"));
            Task<ToolResult> Call(string name, object args, ToolExecutionContext? selected = null) =>
                dispatcher.ExecuteAsync(new(name, CerebralJson.Element(args)), selected ?? context, default);
            var result = await Call("file.read", new { path = source });
            Check(result.Success && JsonDocument.Parse(result.Content).RootElement.GetProperty("content").GetString() == "reference café", "Native read of explicit RO reference succeeds.");
            Check((await Call("file.inspect",new {path=source})).Success,"Native inspection succeeds on RO reference.");
            Check((await Call("file.list",new {path=external})).Success,"Native listing succeeds on RO reference.");
            Check((await Call("file.search",new {path=external,text="reference"})).Success,"Native search succeeds on RO reference.");
            result = await Call("file.write",new {path=Path.Combine(external,"forbidden.txt"),content="no"});
            Check(!result.Success && result.Evidence?.Outcome == ToolExecutionOutcome.Rejected &&
                !File.Exists(Path.Combine(external,"forbidden.txt")), "RO write rejects before a file effect.");
            Check(!(await Call("file.mkdir",new {path=Path.Combine(external,"forbidden-dir")})).Success &&
                !Directory.Exists(Path.Combine(external,"forbidden-dir")), "RO directory creation rejects without mutation.");
            var destination = Path.Combine(project,"copy.txt");
            Check((await Call("file.copy",new {sourcePath=source,destinationPath=destination})).Success &&
                await File.ReadAllTextAsync(destination) == "reference café", "Copy reads RO source and writes RW destination.");
            result=await Call("file.copy",new {sourcePath=destination,destinationPath=Path.Combine(external,"copy-back.txt")});
            Check(!result.Success && !File.Exists(Path.Combine(external,"copy-back.txt")), "Copy destination must be writable.");
            Check(!(await Call("file.edit",new {path=restricted,oldText="unchanged",newText="changed",expectedSha256=read.Sha256})).Success &&
                await File.ReadAllTextAsync(restricted)=="unchanged", "Nested RO edit rejects before preimage/mutation.");
            result=await Call("file.read",new {path=source},context with {Profile=new AgentProfile {AllowedRoots=[project]}});
            Check(!result.Success,"Path permission does not grant semantic tools or capabilities.");
            Check((await Call("shared.read",new {path="reference.txt"})).Success,"Local shared read preserves the chat path ceiling.");
            Check(!(await Call("shared.write",new {path="no.txt",content="no"})).Success &&
                !File.Exists(Path.Combine(external,"no.txt")), "Local shared write cannot drop the path ceiling.");
            Check(!(await Call("process.run",new {executableId="fixture",workingDirectory=external,arguments=Array.Empty<string>()})).Success,
                "Process rejects an RO working directory before launch.");
            var validator=typeof(ToolDispatcher).GetMethod("ValidatePao",BindingFlags.NonPublic|BindingFlags.Static)!;
            JsonElement Pao(object args)
            {
                try { return (JsonElement)validator.Invoke(null,[CerebralJson.Element(args),context,false])!; }
                catch(TargetInvocationException e) when(e.InnerException is not null) { throw e.InnerException; }
            }
            Check(Pao(new {op="read_lines",src=source}).GetProperty("src").GetString()==source,"Direct typed PAO read retains RO source.");
            Reject(()=>Pao(new {op="wr",dst=source}),"Direct typed PAO write rejects RO destination before dispatch.");
            Check(Pao(new {op="git_status",cwd=external}).GetProperty("cwd").GetString()==external,"Fixed read-only Git accepts RO cwd.");
            Check(Pao(new {op="pwsh",src=readOnlyScript,cwd=project}).GetProperty("src").GetString()==readOnlyScript,
                "Direct PAO PowerShell reads its immutable script from an RO root while cwd stays writable.");
            Check(Pao(new {op="dotnet_build",src=readOnlyProject,cwd=project}).GetProperty("src").GetString()==readOnlyProject,
                "Direct PAO build reads an exact RO project while retaining a writable project parent and cwd.");
            Check(Pao(new {op="dotnet_test",src=readOnlyProject,cwd=project}).GetProperty("cwd").GetString()==project,
                "Direct PAO test uses the same split input/output admission.");
            Reject(()=>Pao(new {op="dotnet_build",src=deniedProject,cwd=project}),
                "Direct PAO build still requires Read authority for its project.");
            Reject(()=>Pao(new {op="dotnet_build",src=readOnlyProject,cwd=external}),
                "Direct PAO build still requires Write authority for cwd.");
            Reject(()=>Pao(new {op="dotnet_build",src=externalProject,cwd=project}),
                "A Read-only external project directory cannot become a default output destination.");
            var mapDotnet = typeof(ToolDispatcher).GetMethod("TryMapDotnet",BindingFlags.NonPublic|BindingFlags.Instance)!;
            JsonObject MapDotnet(object args)
            {
                var mapped = new JsonObject();
                try
                {
                    if ((bool)mapDotnet.Invoke(dispatcher,[CerebralJson.Element(args),context,mapped])!) return mapped;
                    throw new InvalidOperationException("The fixture dotnet request was not mapped.");
                }
                catch(TargetInvocationException e) when(e.InnerException is not null) { throw e.InnerException; }
            }
            var mappedBuild=MapDotnet(new {executableId="dotnet",workingDirectory=project,
                arguments=new[]{"build",readOnlyProject}});
            Check(mappedBuild["op"]?.GetValue<string>()=="dotnet_build" &&
                mappedBuild["src"]?.GetValue<string>()==readOnlyProject &&
                mappedBuild["cwd"]?.GetValue<string>()==project,
                "Semantic dotnet mapping accepts a Read-only project file with writable cwd/output parent.");
            var mappedDiscovery=MapDotnet(new {executableId="dotnet",workingDirectory=project,
                arguments=new[]{"test"}});
            Check(mappedDiscovery["op"]?.GetValue<string>()=="dotnet_test" &&
                mappedDiscovery["src"]?.GetValue<string>()==readOnlyProject,
                "Directory discovery revalidates its selected Read-only project child.");
            Reject(()=>MapDotnet(new {executableId="dotnet",workingDirectory=project,
                arguments=new[]{"build",deniedProject}}),
                "Semantic dotnet mapping cannot consume a project without Read authority.");
            Reject(()=>MapDotnet(new {executableId="dotnet",workingDirectory=external,
                arguments=new[]{"build",readOnlyProject}}),
                "Semantic dotnet mapping cannot use a Read-only cwd.");
            Reject(()=>MapDotnet(new {executableId="dotnet",workingDirectory=project,
                arguments=new[]{"build",externalProject}}),
                "A mapped external project with a Read-only output parent is rejected.");
            var missing=Path.Combine(root,"missing-shared");
            settings.SharedDirectory=missing;
            var missingContext=context with {PathAccess=new(1,[new(missing,ScopedPathAccess.Read)])};
            Check(!(await Call("shared.read",new {path="x.txt"},missingContext)).Success && !Directory.Exists(missing),
                "A scoped shared read never creates its missing folder.");
            Check(profile.AllowedRoots.SequenceEqual([project]),"Explicit chat paths never mutate reusable profile roots.");
            return checks;
        }
        finally
        {
            var full=Path.GetFullPath(root);
            if(!ScopedFilePolicy.IsWithin(full,temporaryParent) || !Path.GetFileName(full).StartsWith("cerebral-path-access-",StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup target changed.");
            Directory.Delete(full,recursive:true);
        }
    }
}
