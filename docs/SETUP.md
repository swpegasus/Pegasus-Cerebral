# First setup

Use this guide after [building Worker, Configuration and Main](BUILD.md). It describes a source-preview workspace with its own data directory. It does not register Windows services or turn a source build into an installed upgrade.

## 1. Acquire a model and runtime

Download a compatible chat GGUF and a Windows `llama-server.exe` runtime with its matching DLLs. See [Models and capacity](MODELS.md) for the baseline model, download pages, directory layout and runtime requirements.

Use absolute paths. Do not point the preview at someone else's application data or an existing installed database.

## 2. Start Main with an explicit source data folder

From the repository root, replace the model/runtime examples with your own existing files:

```powershell
$cerebralData = Join-Path $env:LOCALAPPDATA 'Pegasus\Cerebral\SourcePreview'
$modelPath = 'C:\AI\Models\Qwen3.5-9B\Qwen3.5-9B-Q5_K_M.gguf'
$llamaServer = 'C:\AI\Runtimes\llama\llama-server.exe'

dotnet run --project .\src\Cerebral.Orchestrator\Cerebral.Orchestrator.csproj -c Release --no-build -- --source-local "$cerebralData" --model "$modelPath" --llama-server "$llamaServer"
```

The source-local startup path:

1. Binds the selected data directory and starts or discovers its local source Worker.
2. Saves that source connection and the supplied runtime path.
3. Registers a supplied GGUF by reference (`copy=false`) if that path is not already registered, even when a lead profile already exists.
4. Creates the initial lead profile when none exists and loads the model when local resources are active.

The starter registration uses **one parallel request** and automatic context sizing as conservative defaults. It does not copy a developer's live eight-slot configuration. A paused or draining resource state is respected. An existing lead route is preserved; ambiguous multiple-lead setup is refused. Explicit `--model` and `--llama-server` arguments can still add a model registration or update the saved runtime path in an existing source store, so omit them on later launches unless those changes are intended.

Source mode enables external-command support in its isolated developer store. That is not a grant of Cortex Full control, operating-system administrator rights or every tool to every profile. Review [permissions](DATA-AND-PERMISSIONS.md) before enabling execution.

On subsequent launches with the same data folder, omit `--model` and `--llama-server` once the saved configuration is correct:

```powershell
dotnet run --project .\src\Cerebral.Orchestrator\Cerebral.Orchestrator.csproj -c Release --no-build -- --source-local "$cerebralData"
```

**Keep the explicit source-local selection.** Simply running a development executable does not select isolated storage. Normal installed launches use registered roots and enrollment instead.

## 3. Open Configuration

Use **Settings** in Main. This preserves the current source connection/binding. If starting Configuration separately, provide the same data root:

```powershell
dotnet run --project .\src\Cerebral.Integrator\Cerebral.Integrator.csproj -c Release --no-build -- --source-local "$cerebralData"
```

Configuration groups the source surfaces as follows:

| Group | Useful pages |
|---|---|
| General | General, Appearance, Chats, Backup & restore |
| Compute & agents | Machines, Models, Agent profiles, Roles & instructions |
| Plugins & tools | Desktop plugins, Service extensions, Skills, Tools & MCP |
| Activity | Active work, Idle checklists |
| Connections | Cortex, User connector, Peer sharing |
| Advanced | Folders & runtime, Resources & queues, History & cache, System access, Engine, Local recovery, Command console |
| About | Pegasus Cerebral version, credits, links and licensing information |

Some controls require a connected Worker and compatible host capabilities. A visible page is not proof that its external backend is configured.

## 4. Check model and role setup

In **Compute & agents → Models**, select the intended machine and model. **Add models…** supports scanning the configured folder, importing GGUF files, browsing Hugging Face, direct URLs and Cerebral model packages. Local import can reference a file; remote import/upload has a different transfer boundary.

**Set up Cerebral…** loads the selected model and creates the starter lead, coding, analysis, writing and compaction profiles when initial setup is needed. When a lead already exists, adjust the existing profiles rather than expecting this action to overwrite them.

Use **Details / more… → Runtime and chat settings** to review context, concurrency and related model settings. Use **Load / unload** deliberately; changing capacity can affect work using the model.

## 5. Try conversation, then add optional features

Wait for a connected, chat-ready state and send a simple greeting in Main. A greeting should not require desktop automation or general-purpose execution tools. Inspect Activity when work is delegated; a model's statement that it delegated is not an execution receipt.

Add only the [plugins](PLUGINS.md) you need. Desktop installation, Worker service packages and hosted providers have separate setup paths.

[Cortex](https://github.com/swpegasus/Pegasus-Cortex/releases/tag/v2.2026.11.22.03.54.A) is optional and separately installed. Configure your own approved connection. Source-local mode does not automatically create a registered installed-user Cortex enrollment. Local chat can be set up independently.

If chat readiness, model loading or a plugin fails, use the specific guidance in [Troubleshooting](TROUBLESHOOTING.md). Preserve drafts and uncertain work while diagnosing it.
