using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class Configuration(StateStore store)
{
    internal StateStore State => store;
    public HostSettings Get()
    {
        Interlocked.Increment(ref getInvocationCount);
        if (sourceRecoverySnapshot?.Invoke() is { } recoverySnapshot) return recoverySnapshot;
        return store.Locked(() =>
        {
            var installedProjects = RegisteredProjectsRoot(store);
            var value = store.Get<HostSettings>("settings", "host");
            if (value is not null)
            {
                var changed = false;
                if (string.IsNullOrWhiteSpace(value.InstallationId))
                { value.InstallationId = Guid.NewGuid().ToString("N"); changed = true; }
                if (value.AllowedLogicalProcessors is null) { value.AllowedLogicalProcessors = []; changed = true; }
                if (string.IsNullOrWhiteSpace(value.ModelDirectory)) { value.ModelDirectory = Path.Combine(store.DirectoryPath, "models"); changed = true; }
                if (string.IsNullOrWhiteSpace(value.SharedDirectory)) { value.SharedDirectory = Path.Combine(store.DirectoryPath, "Shared"); changed = true; }
                if (string.IsNullOrWhiteSpace(value.ToolsDirectory)) { value.ToolsDirectory = Path.Combine(store.DirectoryPath, "Tools"); changed = true; }
                if (installedProjects is not null &&
                    !string.Equals(value.ProjectsDirectory, installedProjects, ScopedFilePolicy.PathComparison))
                {
                    ReplaceConfiguredProjectRoot(value.ProjectsDirectory, installedProjects);
                    value.ProjectsDirectory = installedProjects;
                    changed = true;
                }
                if (string.IsNullOrWhiteSpace(value.ProjectsDirectory))
                {
                    value.ProjectsDirectory = ValidateDataDirectory(Path.Combine(store.DirectoryPath, "Projects"), "Project");
                    Directory.CreateDirectory(value.ProjectsDirectory); changed = true;
                }
                // Early installed builds created the default agents before setup moved the project
                // repository from the data tree to the configured root (normally C:\CerebralProjects).
                // Keep only that exact managed root in sync; custom/narrow roots remain owner-controlled.
                ReplaceConfiguredProjectRoot(Path.Combine(store.DirectoryPath, "Projects"), value.ProjectsDirectory);
                if (installedProjects is not null) EnsureRegisteredProjectsGrant(installedProjects);
                if (string.IsNullOrWhiteSpace(value.LlamaServerPath))
                {
                    var candidate = ConventionalRuntimePath();
                    // Discovery supplies a default path only; models.load verifies the runtime and its dependencies.
                    if (File.Exists(candidate)) { value.LlamaServerPath = candidate; changed = true; }
                }
                foreach (var executable in DiscoverStandardExecutables(value.ToolsDirectory))
                    if (!value.Executables.Any(item => item.Id.Equals(executable.Id, StringComparison.Ordinal)))
                    { value.Executables.Add(executable); changed = true; }
                // Rename only the former shipped lead name. User-defined profile names remain untouched.
                foreach (var lead in store.List<AgentProfile>("agent").Where(x => x.Role == "lead" &&
                             x.Name.Equals("Orchestrator", StringComparison.OrdinalIgnoreCase)))
                {
                    lead.Name = "Cerebral"; lead.Revision++; store.Put("agent", lead.Id, lead);
                }
                // Older installations represented the default implicitly as the sole lead profile's model.
                // Migrate that state once so later switches can distinguish followers from pinned helpers.
                if (string.IsNullOrWhiteSpace(value.DefaultModelId))
                {
                    var lead = store.List<AgentProfile>("agent").SingleOrDefault(x => x.Role == "lead");
                    if (lead is not null)
                    {
                        value.DefaultModelId = lead.ModelId; value.DefaultModelWorkerId = lead.ModelWorkerId; changed = true;
                        foreach (var profile in store.List<AgentProfile>("agent").Where(x => x.WorkerId == "local" && x.ModelWorkerId == lead.ModelWorkerId && x.ModelId == lead.ModelId))
                        {
                            if (profile.FollowsDefaultModel) continue;
                            profile.FollowsDefaultModel = true; profile.Revision++; store.Put("agent", profile.Id, profile);
                        }
                    }
                }
                if (changed) { value.Revision++; store.Put("settings", "host", value); }
                return value;
            }
            // An extracted portable payload declares sibling mutable roots in installation.ini.
            // Apply them only for a new store that owns this exact portable Data directory;
            // an existing settings record or an explicit --data root keeps its saved paths.
            var portableRoot = InstallationPaths.IsPortable &&
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(store.DirectoryPath)).Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(InstallationPaths.DataDirectory)),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                ? InstallationPaths.InstallDirectory : null;
            value = new() { InstallationId = Guid.NewGuid().ToString("N"), AccessKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
                ModelDirectory = Path.Combine(portableRoot ?? store.DirectoryPath, portableRoot is null ? "models" : "Models") };
            value.SharedDirectory = Path.Combine(portableRoot ?? store.DirectoryPath, "Shared");
            value.ToolsDirectory = Path.Combine(portableRoot ?? store.DirectoryPath, "Tools");
            value.ProjectsDirectory = installedProjects ??
                ValidateDataDirectory(Path.Combine(portableRoot ?? store.DirectoryPath, "Projects"), "Project");
            EnsureMutableDirectories(value, store.DirectoryPath);
            var runtime = ConventionalRuntimePath();
            if (File.Exists(runtime)) value.LlamaServerPath = runtime;
            value.Executables.AddRange(DiscoverStandardExecutables(value.ToolsDirectory));
            // Record discovery separately from MCP transport selection. Enrollment, connector
            // binding, grants and knowledge spaces remain explicit.
            _ = CortexFirstRunDiscovery.InspectRegistered().SaveInitialTo(value);
            store.Put("settings", "host", value); return value;
        });
    }
    private static string ConventionalRuntimePath() => Path.Combine(AppContext.BaseDirectory, "runtime", OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");
    internal static void EnsureMutableDirectories(HostSettings value, string dataRoot)
    {
        // Program binaries and packaged service plugins remain in the replaceable application root. Host-owned
        // models, tools, projects, shared files, plugin state and attachment artifacts survive an atomic upgrade.
        foreach (var directory in new[]
                 {
                     value.ModelDirectory, value.ProjectsDirectory, value.SharedDirectory, value.ToolsDirectory,
                     Path.Combine(dataRoot, "PluginData"), Path.Combine(dataRoot, "Artifacts", "Attachments")
                 })
            Directory.CreateDirectory(directory);
    }
    private static string? DiscoverDotnet()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root)
            candidates.Add(Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        if (OperatingSystem.IsWindows()) candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"));
        else candidates.AddRange(["/usr/bin/dotnet", "/usr/local/bin/dotnet", "/usr/share/dotnet/dotnet"]);
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }
    private static IEnumerable<ExecutableRegistration> DiscoverStandardExecutables(string toolsDirectory)
    {
        if (DiscoverDotnet() is { } dotnet) yield return new() { Id = "dotnet", Path = dotnet };
        if (!OperatingSystem.IsWindows()) yield break;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var buildTools = Path.Combine(programFiles, "Microsoft Visual Studio", "2026", "BuildTools");
        var candidates = new (string Id, string Path)[]
        {
            ("powershell", Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe")),
            ("git", Path.Combine(programFiles, "Git", "cmd", "git.exe")),
            ("node", Path.Combine(programFiles, "nodejs", "node.exe")),
            ("python", Path.Combine(programFiles, "Python314", "python.exe")),
            ("7zip", Path.Combine(programFiles, "7-Zip", "7z.exe")),
            ("nsis", Path.Combine(programFilesX86, "NSIS", "makensis.exe")),
            ("msbuild", Path.Combine(buildTools, "MSBuild", "Current", "Bin", "MSBuild.exe")),
            ("cmake", Path.Combine(buildTools, "Common7", "IDE", "CommonExtensions", "Microsoft", "CMake", "CMake", "bin", "cmake.exe")),
            ("ninja", Path.Combine(buildTools, "Common7", "IDE", "CommonExtensions", "Microsoft", "CMake", "Ninja", "ninja.exe")),
            ("parakeet", Path.Combine(toolsDirectory, "AI-Dictation", "NeMo-Speech", "bin", "nemo-speech.exe"))
        };
        foreach (var candidate in candidates)
            if (File.Exists(candidate.Path)) yield return new() { Id = candidate.Id, Path = Path.GetFullPath(candidate.Path) };
    }

    public void Save(HostSettings value) => Save(value, installerSetsProjects: false);

    internal void SaveInstalledPaths(HostSettings value) => Save(value, installerSetsProjects: true);

    private void Save(HostSettings value, bool installerSetsProjects)
    {
        store.Locked(() =>
        {
            var current = store.Get<HostSettings>("settings", "host");
            if (current is not null && value.Revision != current.Revision)
                throw new InvalidOperationException("Service settings changed while this copy was open. Refresh and apply the intended change again.");
            if (current is not null)
            {
                // Old clients do not know InstallationId. They may preserve an ordinary installation, but they
                // cannot clear or rewrite standalone-Worker ownership state by omitting newer fields.
                if (string.IsNullOrWhiteSpace(value.InstallationId)) value.InstallationId = current.InstallationId;
                if (value.InstallationId != current.InstallationId)
                    throw new InvalidOperationException("The service installation identity cannot be changed through settings.");
                if (current.StandaloneWorker &&
                    (value.StandaloneWorker != current.StandaloneWorker || value.ProvisioningId != current.ProvisioningId ||
                     value.ProvisioningExpiresAt != current.ProvisioningExpiresAt || value.ManagementControllerId != current.ManagementControllerId ||
                     value.ManagementControllerName != current.ManagementControllerName || value.AccessKey != current.AccessKey))
                    throw new InvalidOperationException("Standalone Worker assignment and its management credential can be changed only through Cerebral Configuration provisioning commands.");
            }
            if (value.StandaloneWorker && value.Coordinator)
                throw new ArgumentException("A standalone Worker cannot own the lead conversation.");
            if (!Uri.TryCreate(value.ListenUrl, UriKind.Absolute, out var uri) || uri.Scheme != "http") throw new ArgumentException("Listen URL must be an absolute HTTP URL.");
            if (!value.AllowLan && !uri.IsLoopback) throw new ArgumentException("Enable LAN access before binding a non-loopback address.");
            if (value.AccessKey.Length < 24) throw new ArgumentException("Use an installation access key of at least 24 characters.");
            if (value.QuestionTimeoutSeconds is < 0 or > 86400 || value.QuestionTimeoutAction is not ("wait" or "assume")) throw new ArgumentException("Question timeout must be 0–86400 seconds and action wait or assume.");
            if (value.WorkflowIdleExpiryDays is < 0 or > HostSettings.MaximumWorkflowIdleExpiryDays)
                throw new ArgumentException("Workflow idle expiry must be 0–3650 days.");
            if (value.MaxConcurrentJobs is < 1 or > 16 || value.QueueCapacity is < 1 or > 1000 || value.PeerQueueCapacity is < 1 or > 1000 ||
                value.ReservedOwnerSlots is < 0 or > 16 || value.ReservedOwnerSlots > value.MaxConcurrentJobs || value.ReservedMemoryMb < 0 ||
                value.DefaultModelIdleMinutes is < 0 or > 10080 || value.MaxExecutableAttemptsPerAssignment is < 1 or > 20)
                throw new ArgumentException("Invalid queue, memory, model-idle, or executable-attempt settings.");
            if (value.MaxLogicalProcessorCount is < 0 or > 1024 || value.AllowedLogicalProcessors is null || value.AllowedLogicalProcessors.Count > 1024 ||
                value.AllowedLogicalProcessors.Any(processor => processor is < 0 or > 1023) ||
                value.AllowedLogicalProcessors.Distinct().Count() != value.AllowedLogicalProcessors.Count)
                throw new ArgumentException("Logical processor policy requires a maximum of 0-1024 and a duplicate-free allowed list using identifiers 0-1023.");
            value.AllowedLogicalProcessors.Sort();
            if (value.PromptCacheDiagnosticsChangePercentagePoints is < 1 or > 100)
                throw new ArgumentException("Prompt-cache diagnostic change threshold must be 1-100 percent.");
            if (value.JobHistoryLimit is < 1 or > 10000 || value.JobHistoryDays is < 1 or > 3650 ||
                value.ActivityHistoryLimit is < 100 or > 50000 || value.ActivityHistoryDays is < 1 or > 3650 ||
                value.EngineEventHistoryLimit is < 1 or > 1_000_000 || value.EngineEventHistoryMegabytes is < 1 or > 16_384 ||
                value.EngineEventHistoryDays is < 1 or > 3650)
                throw new ArgumentException("History limits must be 1-10000 jobs, 100-50000 activity calls, 1-1000000 Engine events, 1-16384 MiB, and 1-3650 days.");
            if (!string.IsNullOrWhiteSpace(value.DefaultModelId)) ValidateId(value.DefaultModelId);
            ValidateId(value.DefaultModelWorkerId);
            if (value.DefaultModelWorkerId != "local" && !value.Workers.Any(x => x.Id == value.DefaultModelWorkerId))
                throw new ArgumentException("The default model worker must identify a configured worker connection.");
            value.ModelDirectory = ValidateDataDirectory(value.ModelDirectory, "Model");
            if (string.IsNullOrWhiteSpace(value.SharedDirectory)) value.SharedDirectory = current?.SharedDirectory ?? throw new ArgumentException("Shared directory is required.");
            if (string.IsNullOrWhiteSpace(value.ProjectsDirectory)) value.ProjectsDirectory = current?.ProjectsDirectory ?? throw new ArgumentException("Project directory is required.");
            if (string.IsNullOrWhiteSpace(value.ToolsDirectory)) value.ToolsDirectory = current?.ToolsDirectory ?? throw new ArgumentException("Portable tools directory is required.");
            value.SharedDirectory = ValidateDataDirectory(value.SharedDirectory, "Shared");
            value.ProjectsDirectory = ValidateDataDirectory(value.ProjectsDirectory, "Project");
            if (!installerSetsProjects && RegisteredProjectsRoot(store) is { } installedProjects &&
                !string.Equals(value.ProjectsDirectory, installedProjects, ScopedFilePolicy.PathComparison))
                throw new ArgumentException("Change the installed Projects directory through Cerebral Setup.");
            value.ToolsDirectory = ValidateDataDirectory(value.ToolsDirectory, "Portable tools");
            foreach (var item in value.BackgroundTasks)
                if (item.IntervalMinutes < 1 || item.MaxRunMinutes is < 1 or > 1440 || string.IsNullOrWhiteSpace(item.ProfileId) || string.IsNullOrWhiteSpace(item.Prompt)) throw new ArgumentException("Background tasks require a profile, prompt and positive time limits.");
            Unique(value.Executables.Select(x => x.Id)); Unique(value.McpServers.Select(x => x.Id)); Unique(value.Workers.Select(x => x.Id)); Unique(value.BackgroundTasks.Select(x => x.Id));
            // Compare-and-swap validation precedes filesystem effects. A stale draft can therefore
            // neither overwrite SQLite nor create a newly typed project/shared directory.
            EnsureMutableDirectories(value, store.DirectoryPath);
            value.Revision = (current?.Revision ?? -1) + 1;
            store.Put("settings", "host", value);
            if (current is not null)
                ReplaceConfiguredProjectRoot(current.ProjectsDirectory, value.ProjectsDirectory);
            return 0;
        });
    }

    internal static string? RegisteredProjectsRoot(StateStore state)
    {
        var install = InstallationPaths.InstallDirectory;
        var data = state.DirectoryPath;
        var projects = InstallationPaths.RegisteredProjectsDirectory(install, data);
        return projects is null ? null : ManagementCommands.ValidateInstallerProjectDirectory(projects, data, install);
    }

    private void EnsureRegisteredProjectsGrant(string projectsDirectory)
    {
        foreach (var profile in store.List<AgentProfile>("agent").Where(profile => profile.WorkerId == "local"))
        {
            if (profile.AllowedRoots.Any(root => string.Equals(
                Path.TrimEndingDirectorySeparator(root), projectsDirectory, ScopedFilePolicy.PathComparison))) continue;
            profile.AllowedRoots.Add(projectsDirectory);
            profile.Revision = checked(profile.Revision + 1);
            store.Put("agent", profile.Id, profile);
        }
    }

    private int ReplaceConfiguredProjectRoot(string? previous, string? current)
    {
        if (string.IsNullOrWhiteSpace(previous) || string.IsNullOrWhiteSpace(current)) return 0;
        string before;
        string after;
        try
        {
            before = Path.TrimEndingDirectorySeparator(Path.GetFullPath(previous));
            after = Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return 0;
        }
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (comparer.Equals(before, after)) return 0;
        var changed = 0;
        foreach (var profile in store.List<AgentProfile>("agent")
                     .Where(x => x.WorkerId == "local"))
        {
            var replaced = false;
            var roots = new List<string>(profile.AllowedRoots.Count);
            foreach (var root in profile.AllowedRoots)
            {
                var normalized = root;
                try { normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); }
                catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { }
                if (comparer.Equals(normalized, before))
                {
                    roots.Add(after);
                    replaced = true;
                }
                else roots.Add(root);
            }
            if (!replaced) continue;
            profile.AllowedRoots = roots.Distinct(comparer).ToList();
            profile.Revision = checked(profile.Revision + 1);
            store.Put("agent", profile.Id, profile);
            changed++;
        }
        return changed;
    }

    public HostSettings ProvisionStandalone(string name, string listenUrl, bool autoSelectAvailablePort,
        DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl))
            throw new ArgumentException("Worker name is required and must be at most 200 characters.");
        var endpoint = ValidateStandaloneListenUrl(listenUrl);
        var at = now ?? DateTimeOffset.UtcNow;
        return store.Locked(() =>
        {
            var existing = store.Get<HostSettings>("settings", "host");
            if (existing is not null && !existing.StandaloneWorker)
                throw new InvalidOperationException("An existing integrated Cerebral data store cannot be converted into a standalone Worker.");
            var current = existing ?? Get();
            if (current.StandaloneWorker && !string.IsNullOrWhiteSpace(current.ManagementControllerId))
                return current; // Repair and upgrade preserve the established Cerebral assignment.
            if (current.StandaloneWorker && current.ProvisioningExpiresAt > at && !string.IsNullOrWhiteSpace(current.ProvisioningId))
                return current; // Rerunning setup preserves the invitation already handed to the owner.
            current.Name = name.Trim();
            current.Coordinator = false;
            current.ListenUrl = endpoint;
            current.AllowLan = true;
            current.AutoSelectAvailablePort = autoSelectAvailablePort;
            current.StandaloneWorker = true;
            current.ManagementControllerId = null;
            current.ManagementControllerName = null;
            current.ProvisioningId = Guid.NewGuid().ToString("N");
            current.ProvisioningExpiresAt = at.AddDays(7);
            current.AccessKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            current.Revision++;
            store.Put("settings", "host", current);
            return current;
        });
    }

    public HostSettings PinStandaloneEndpoint(string listenUrl)
    {
        var endpoint = ValidateStandaloneListenUrl(listenUrl);
        return store.Locked(() =>
        {
            var current = Get();
            if (!current.StandaloneWorker || !string.IsNullOrWhiteSpace(current.ManagementControllerId))
                throw new InvalidOperationException("Only an unassigned standalone Worker can finalize its bootstrap endpoint.");
            current.ListenUrl = endpoint;
            current.AutoSelectAvailablePort = false;
            current.Revision++;
            store.Put("settings", "host", current);
            return current;
        });
    }

    public WorkerManagementStatus ManagementStatus() => Status(Get());

    public WorkerManagementStatus ClaimStandalone(string provisioningId, string controllerInstallationId,
        string controllerName, string replacementManagementKey, DateTimeOffset? now = null)
    {
        ValidateId(controllerInstallationId);
        if (string.IsNullOrWhiteSpace(controllerName) || controllerName.Length > 200 || controllerName.Any(char.IsControl))
            throw new ArgumentException("Cerebral controller name is invalid.");
        if (replacementManagementKey.Length is < 24 or > 512 || replacementManagementKey.Any(char.IsWhiteSpace) || replacementManagementKey.Any(char.IsControl))
            throw new ArgumentException("Replacement Worker management key is invalid.");
        var at = now ?? DateTimeOffset.UtcNow;
        return store.Locked(() =>
        {
            var current = Get();
            if (!current.StandaloneWorker) throw new InvalidOperationException("This Cerebral service is not a standalone Worker.");
            if (!string.IsNullOrWhiteSpace(current.ManagementControllerId))
            {
                if (current.ManagementControllerId == controllerInstallationId &&
                    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(current.AccessKey), Encoding.UTF8.GetBytes(replacementManagementKey)))
                    return Status(current);
                throw new InvalidOperationException("This Worker is already assigned to another Cerebral configuration.");
            }
            if (current.ProvisioningId != provisioningId || current.ProvisioningExpiresAt is null || current.ProvisioningExpiresAt <= at)
                throw new InvalidOperationException("The Worker provisioning invitation is invalid or expired. Run Worker repair to issue a new invitation.");
            current.ManagementControllerId = controllerInstallationId;
            current.ManagementControllerName = controllerName.Trim();
            current.ProvisioningId = null;
            current.ProvisioningExpiresAt = null;
            current.AccessKey = replacementManagementKey;
            current.Revision++;
            store.Put("settings", "host", current);
            return Status(current);
        });
    }

    private static WorkerManagementStatus Status(HostSettings value) => new(value.InstallationId,
        !value.StandaloneWorker ? "integrated" : string.IsNullOrWhiteSpace(value.ManagementControllerId) ? "awaiting-claim" : "assigned",
        value.ManagementControllerId, value.ManagementControllerName, value.ProvisioningExpiresAt);

    private static string ValidateStandaloneListenUrl(string listenUrl)
    {
        if (!Uri.TryCreate(listenUrl, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttp ||
            endpoint.Host != "0.0.0.0" || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 ||
            endpoint.Fragment.Length != 0 || endpoint.AbsolutePath.Trim('/').Length != 0 || endpoint.Port is < 1 or > 65535)
            throw new ArgumentException("Standalone Worker provisioning requires an IPv4 wildcard HTTP origin with an explicit port.");
        return endpoint.GetLeftPart(UriPartial.Authority);
    }
    private static string ValidateDataDirectory(string directory, string label)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Contains('\0')) throw new ArgumentException(label + " directory must be an absolute service-accessible path.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var program = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        // Published programs share a replaceable platform/install root with their sibling apps.
        if (Path.GetFileName(program).Equals("Worker", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            program = Path.GetDirectoryName(program)!;
        var relative = Path.GetRelativePath(program, full);
        if (relative == "." || (!Path.IsPathFullyQualified(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            throw new ArgumentException(label + " files must remain outside replaceable program output.");
        return full;
    }
    public static void ValidateProfile(AgentProfile profile)
    {
        ValidateId(profile.Id);
        ValidateId(profile.WorkerId); ValidateId(profile.ModelWorkerId);
        if (profile.RemoteProfileId is not null) ValidateId(profile.RemoteProfileId);
        if (string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.ModelId)) throw new ArgumentException("Agent name and model are required.");
        if (profile.ContextTokens is < AgentTokenLimits.MinimumContextTokens or > 1048576 || profile.ResponseTokens is < AgentTokenLimits.MinimumResponseTokens or > AgentTokenLimits.MaximumResponseTokens || profile.ResponseTokens >= profile.ContextTokens || profile.MaxSteps is < 1 or > 10000 || profile.MaxSeconds is < 1 or > 86400 || !double.IsFinite(profile.Temperature) || profile.Temperature is < 0 or > 2 || profile.IdleMinutes < -1 || profile.MemoryBudgetMb < 0) throw new ArgumentException("Invalid context, response, execution, memory or temperature limits.");
        if (profile.ReasoningMode is not ("off" or "on") || profile.ReasoningTokens is < AgentTokenLimits.MinimumReasoningTokens or > AgentTokenLimits.MaximumReasoningTokens || profile.ResponseTokens > AgentTokenLimits.MaximumVisibleResponse(profile.ContextTokens, profile.ReasoningMode, profile.ReasoningTokens)) throw new ArgumentException("Invalid reasoning mode or budget; leave context space for instructions and outputs.");
        if (profile.ReasoningTokenCeiling is not 0 &&
            (profile.ReasoningTokenCeiling < AgentTokenLimits.MinimumReasoningTokens ||
             profile.ReasoningTokenCeiling > AgentTokenLimits.MaximumReasoningTokens ||
             profile.ReasoningTokenCeiling < profile.ReasoningTokens))
            throw new ArgumentException("The owner reasoning ceiling must be zero for the existing fixed budget or at least the default reasoning budget.");
        if (profile.TaskBudgetPolicy is { } taskBudget &&
            (taskBudget.SchemaVersion != 1 ||
             taskBudget.MaxContextTokens is null && taskBudget.MaxResponseTokens is null ||
             taskBudget.MaxContextTokens is { } contextCeiling &&
                 (contextCeiling < profile.ContextTokens || contextCeiling > 1_048_576) ||
             taskBudget.MaxResponseTokens is { } outputCeiling &&
                 (outputCeiling < profile.ResponseTokens || outputCeiling > AgentTokenLimits.MaximumResponseTokens) ||
             taskBudget.MaxResponseTokens is { } requestedOutput &&
                 requestedOutput > AgentTokenLimits.MaximumVisibleResponse(
                     taskBudget.MaxContextTokens ?? profile.ContextTokens, profile.ReasoningMode,
                     profile.ReasoningMode == "on" ? profile.ReasoningTokenCeiling == 0
                         ? profile.ReasoningTokens : profile.ReasoningTokenCeiling : 0)))
            throw new ArgumentException("The owner task budget policy is invalid or leaves no input and response headroom.");
        AgentTokenLimits.ValidateCompactionReduction(profile.CompactionReductionPercent, nameof(profile.CompactionReductionPercent));
        AgentTokenLimits.ValidateCompactionTrigger(profile.CompactionTriggerPercent, nameof(profile.CompactionTriggerPercent));
        if (profile.Role is not ("lead" or "helper" or "compactor")) throw new ArgumentException("Role must be lead, helper or compactor.");
        ModelCapabilityPolicy.ValidateRequirements(profile);
        if (profile.PluginSettings.Count > 32 || profile.PluginSettings.Any(item =>
                string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 100 || item.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))))
            throw new ArgumentException("Agent plugin settings require at most 32 named object values of at most 64 KiB each.");
        foreach (var value in profile.PluginSettings.Values) ValidatePluginSettingsValue(value);
        foreach (var root in profile.AllowedRoots) if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("Workspace roots must be absolute paths on the executing machine.");
    }
    public static void ValidatePluginSettingsValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            JsonSerializer.SerializeToUtf8Bytes(value, CerebralJson.Options).Length > 65_536)
            throw new ArgumentException("Agent plugin settings require object values of at most 64 KiB each.");
        RejectDuplicatePluginSettingsFields(value);
    }
    private static void RejectDuplicatePluginSettingsFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new ArgumentException("Agent plugin settings cannot contain duplicate fields.");
                RejectDuplicatePluginSettingsFields(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicatePluginSettingsFields(item);
    }
    public static void ValidateId(string id)
    {
        if (id.Length is < 1 or > 100 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')) throw new ArgumentException("Identifiers allow 1–100 ASCII letters, digits, dots, hyphens and underscores.");
    }
    private static void Unique(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids) { ValidateId(id); if (!seen.Add(id)) throw new ArgumentException("Duplicate configuration identifier: " + id); }
    }
}
