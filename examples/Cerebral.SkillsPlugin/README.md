# Skills manager

Manage reusable **instruction documents** for Cerebral: discover local definitions, preview them, install them disabled, then enable them for an explicit scope. A skill does not add tools, permissions, model access or a new execution service.

**Desktop package:** `pegasus.skills`, version **0.1.0**, **disabled by default**.

## Build and enable the manager

Requires Windows x64, .NET 10 and compatible Core/desktop-plugin SDK assemblies from this source tree.

```powershell
dotnet build .\examples\Cerebral.SkillsPlugin\Cerebral.SkillsPlugin.csproj -c Release
```

Run from the repository root. Keep the native DLL, copied `desktop-plugin.json` and the complete bundled `Skills/` directory from default `bin/Release/net10.0-windows/` output. The host supplies Core and its SDK.

In Configuration → **Desktop plugins**, **Install…** the built manifest/package, review installation disabled, then select **Manage → Enable → Go**. Open Main's **Skills** composer action or the plugin's **Skills** Configuration page. Refresh or reopen the relevant desktop host if its view is not active. Loading the manager does not need a Worker restart, but it needs a connected Worker with the existing `skills.*` catalog operations.

## Install and assign a skill

1. **Refresh** and read the displayed editable/bundled roots, Worker identity, last scan and validation errors.
2. Select a definition to preview its actual text, identity/version and declared assets. Search/group filters narrow the list; **Select group / results** selects the current results.
3. Choose **Install selected**. A new catalog installation stays **disabled and unassigned**.
4. Select an **Enable scope**: global, role, profile or project. Role values are exactly `lead`, `helper`, `compactor`; profile/project scopes require exact IDs. **Keep assignments**, the default, retains existing bindings rather than inventing a scope.
5. Choose **Enable selected**. **Disable selected** keeps definitions and assignments stored; **Uninstall selected…** removes the exact owned catalog record.

Discovery alone does not install a new file or authorize its instructions. Once a source is installed/owned, observed valid edits can update it while preserving its existing enablement/bindings. Missing or invalid owned source disables that skill; fix it and explicitly enable again. External catalog changes are checked, not overwritten blindly.

## Folders, documents and groups

Bundled definitions live in the package's read-only `Skills/` tree. Editable/imported definitions use the host-selected plugin data directory's `Skills/` tree. Use the paths displayed in the manager; do not edit immutable code-package generations or hardcode another machine's data path.

A definition normally has `skill.json` plus `SKILL.md`. SKILL.md-only sources can use supported scalar metadata. Markdown is strict UTF-8, at most 32 KiB; manifest JSON is at most 64 KiB. The parser supports a bounded scalar frontmatter subset, not general YAML.

Declared assets are passive document/image files, at most 32 per skill and 1 MiB each. Scripts, hooks and installers are not run. Links, traversal and unsupported fields produce validation errors.

**Save selection as group** persists a local named selection. **Import group…** previews conflicts with Keep, Replace or SideBySide decisions. Replacement preserves current enablement/assignments; new identities stay disabled. Imports report per-item outcomes and are not an atomic multi-skill transaction. ZIP admission is bounded to 128 skills, 512 files and 16 MiB expanded.

**Export group…** includes selected definitions, declared assets and mandatory license notices. It excludes application configuration/history/permission records, but authored text or assets can themselves contain sensitive information: review them before sharing.

## Persistence and safety

The private manager index records source ownership, exclusions, groups and pending outcomes. The Worker's core skill catalog owns actual enabled state and bindings. Closing/disabling the manager stops its watcher; it does **not** disable previously installed skills or erase instructions already accepted into a run.

The debounced watcher operates while the manager is mounted. **Refresh** remains available if watcher events are lost. Intentionally uninstalled definitions are not silently reinstalled by a scan. Optional uninstall file cleanup is limited to unchanged import-owned files; bundled, modified and unknown files are preserved.

## Troubleshooting and bundled licenses

- **Different Worker / connection review required:** use **Review connection…** and verify the intended installation. An old index grants no authority over another catalog.
- **Invalid document:** inspect the row diagnostic; fix encoding, metadata, size or asset declarations, then Refresh.
- **Cannot enable:** install first and choose an explicit valid assignment.
- **Unconfirmed install/configure/remove:** inspect the original owner/catalog. Do not delete the index or replay a mutation to conceal uncertainty.
- **Manager disabled but skill still influences new work:** inspect the Worker's per-skill enabled state; manager availability and skill enablement are separate.

The bundled CerebralBase contains eight adapted engineering disciplines. Their source and MIT attribution are documented in [Skills/THIRD-PARTY-NOTICES.md](Skills/THIRD-PARTY-NOTICES.md); each retains its upstream license asset. The manager does not fetch upstream updates automatically.

Source: [SkillsPlugin.cs](SkillsPlugin.cs), [SkillsManager.cs](SkillsManager.cs), [SkillDocument.cs](SkillDocument.cs), [SkillFolderCatalog.cs](SkillFolderCatalog.cs), [SkillGroups.cs](SkillGroups.cs), [desktop manifest](desktop-plugin.json).

## Preview and license

These instructions describe the published source. This documentation pass did not restore, compile, install or run the plugin; it is not a fresh-checkout or installed-runtime qualification. Use a host built from a compatible SDK and retain integrity/permission checks when diagnosing failures.

First-party code is covered by the [Pegasus Community License 1.0](../../LICENSE); third-party components retain their own terms. Created by Shayne Winter with development assistance from OpenAI Codex and Pegasus Cerebral. See [plugin lifecycle and permissions](../../docs/PLUGINS.md) and [building Cerebral](../../docs/BUILD.md).
