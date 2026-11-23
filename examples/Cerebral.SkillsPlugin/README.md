# Skills for Pegasus Cerebral

Optional desktop manager over Cerebral's existing instruction-skills catalog. No new tools, dispatcher, service, model, permissions or credential store.

Open the **Skills** action in Main or the plugin's **Skills** Configuration page. The current folder paths, Worker installation identity, last scan, watcher state and validation errors are visible. Existing Configuration → Instruction skills remains a management fallback.

## Folders and availability

- Bundled definitions are read from the replaceable plugin payload's Skills/ subfolders. Normal UI never writes there.
- Editable/imported definitions use the SDK-selected plugin DataDirectory/Skills. The normal installed default follows Cerebral's selected user-data root under ProgramData; explicit, private, source and portable roots remain selected exactly as provided by the host. No path is hardcoded or repointed.
- Startup/manual scans and the debounced watcher discover additions, changes and removals. Watching requires the desktop host; closing the floating manager or unloading its view pauses watching. Reopening resumes it. There is no Worker daemon.
- Discovery populates the manager only. **Install** admits the definition disabled and unassigned. **Enable** uses an explicit global, role, profile or project assignment. Roles are exactly lead/helper/compactor. Keep assignments preserves existing scope; selecting a scope explicitly replaces it.
- Valid file updates preserve saved enablement and bindings. Missing/invalid owned sources are disabled when observed; after fixing them, explicitly Enable. External catalog edits are checked by revision/source/content and are not overwritten automatically.
- Uninstall stays uninstalled across scans and package-version paths. Bundled skill exclusions use stable origin/ID. External owner removal is not undone. Explicit Install clears the selected exclusion.
- Disabling/closing this manager stops its watcher, without disabling/removing already persisted skills. Core per-skill settings remain authoritative.

Each skill normally has skill.json (the existing schemaVersion/id/name/version/description/triggers SkillManifest) and SKILL.md. SKILL.md-only folders can be indexed from scalar name/description metadata; installation preserves the derived stable identity in the private index. JSON IDs/version metadata travel with export.

## Supported documents and assets

Markdown is strict UTF-8, at most 32 KiB. Manifest JSON is at most 64 KiB. Frontmatter supports documented scalar keys: name, description, id, version, triggers, assets, license, author, origin. Values are plain text, single quotes, JSON-compatible double quotes, or two-space-indented literal/folded blocks. Trigger/asset lists are comma/semicolon/newline-separated scalars. Nested YAML, collections, tags, aliases, extra keys and duplicate keys are rejected with a validation error. This is deliberately not a full YAML parser.

Declared assets are passive .md/.txt/.json/.csv/.tsv/.yaml/.yml documents or .png/.jpg/.jpeg/.gif/.webp images, at most 32 per skill and 1 MiB each. Relative paths are bounded and links/traversal/reserved names are refused. Assets do not confer file access or command rights; agents continue using only existing allowed file/tool routes. No scripts/hooks/installers are executed or registered.

## Groups and safe changes

Filter/search, select results, and use the bulk controls for install/enable/disable/uninstall. Save a selection as a named group. Import previews every ID conflict with Keep, Replace or SideBySide choices. Replace preserves current enablement/bindings; a new ID installs disabled. A case-only collision cannot be replaced. The group records per-item outcomes, including unattempted items after failure; it does not promise an atomic multi-skill Worker transaction.

Group ZIPs contain only group.json, skills/<id>/skill.json, SKILL.md and explicitly declared passive assets. The shared Core BoundedContentZipArchive handle validates names/links/collisions, bounds actual reads and extracts the same retained archive only after preview to an owned empty temporary directory. Domain limits are 128 skills, 512 files and 16 MiB expanded. No code-package admission is bypassed. Imported content uses a new private data subfolder; replaced source copies remain preserved rather than overwritten.

Exports contain selected definitions and all declared assets, including mandatory license/source notices for derived bundled defaults. They exclude application settings, credentials, history, permission records and unknown files. Review authored text before export: an arbitrary document can itself contain a secret. Export never writes into the plugin code payload.

Uninstall removes only the exact owned catalog record after current installation/revision/source/content checks. Optional file removal is limited to unchanged import-owned files. Bundled, modified and unknown files are preserved; no recursive user-data deletion occurs.

A different Worker installation requires explicit connection review. An old ownership index is not authority over a foreign catalog. Lost/invalid mutation replies remain visibly unconfirmed and are never retried automatically; Refresh can confirm a matching install/configuration outcome. Unconfirmed removal is retained for original-owner inspection, without inferring success from temporary absence.

## Included base and licensing

CerebralBase bundles eight Cerebral-adapted disciplines: requirements clarification, diagnosis, test-driven changes, code design, review, primary-source research, verified handoff, and agent guidance writing. Source: Matt Pocock's skills repository, pinned at abeeaf95660ba9e8b7c753eb38f441ca707a8823. Copyright (c) 2026 Matt Pocock; MIT. Each skill includes the full unchanged LICENSE.txt and attribution; see Skills/THIRD-PARTY-NOTICES.md for original paths/blobs and adaptation/exclusion notes. No automatic upstream download/update occurs.

## Integration and evidence boundary

The host supplies Core and the desktop SDK. Source/package this plugin through the existing Cerebral plugin installer; no installed hand-copy is part of this work. The shared archive API and Worker live skill-input fence must ship in the coordinated maintained source/build. The manager does not add those host features or erase previously received agent context.

This source unit and its focused fixtures are not installed acceptance. Build/test execution, real UI/Worker behavior, packaging and activation require the coordinator's released verification/deployment phase. No live model, actual user-data, registry, ACL or service action is needed by the focused synthetic fixtures.
