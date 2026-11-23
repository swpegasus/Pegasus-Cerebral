# Chat Icons 1.0.3 plugin and portable asset pack

Chat Icons adds an **Icons** button through the existing text-entry plugin contract. It opens
a small scrollable picker with a preview, accessible label, Unicode insertion and **Copy reference**.
Insertion replaces the target field's current selection at the cursor; it never sends a message,
replaces the complete composer draft, embeds SVG markup, or writes a model-generated status.
Conversation changes close the picker; stale/unavailable entries and host rejection remain bounded.
The Icons action beside the intended field captures its text, selection and conversation. The compact
Symbols anchor belongs to the composer accessory; the picker opens in a separate bounded popup.
Use Icons beside the field to begin a new capture. The unbound Symbols anchor does not choose a target.
Each reopen starts with an empty search and All symbols. Close, Escape, outside dismissal, owner
retirement and target changes end the capture.
Successful insertion is not replayed or reported failed solely because focus restoration fails.

The plugin contributes optional user/assistant/tool message decorations without replacing original
messages. IDs, roles, copy text, persistence, role grouping and model input remain host-owned.
Activity call/status replacement is unavailable through the current public desktop contracts and
remains a followup. No host WPF controls or arbitrary message trees are patched.

Its Configuration panel edits five quick-insert role mappings and eleven semantic mappings as one
package-owned schema-1 document in the central Cerebral settings database. The aggregate contains
only exact built-in asset IDs and is explicitly nonsecret/portable, with a 16 KiB bound. Save roles
preserves the committed meaning map; Save meanings preserves committed roles, including when the
other editor has unsaved changes. Save all choices applies both displayed maps. Every write uses
the exact stored revision through the existing owner command bridge. A Changed=false acknowledgement
must preserve the complete observed state, including opaque active/pending values, origins/reasons,
validation authority and projection/omission metadata; a contradiction remains an unknown outcome.
The editor shows existing validated catalog glyphs beside their readable labels when that local
pack is available, with a label-only fallback and no new icon/resource dependency.

Main and Configuration have separate native package catalogs but use the same central global icon
preference coordinate. Verify the selected editor version separately from the Main picker version.

Opening or reloading the panel performs no save, admission or legacy migration. Unsupported active
versions disable saving; unsupported pending imports stay unchanged. Applying a selected supported
saved import removes only that pending entry. A lost/conflicting acknowledgement preserves the
draft and requires an explicit reload before another save; no mutation is automatically repeated.
Unload cancels pending operations, and the original owner transport remains bound by the host.

Load existing local choices explicitly decodes DataDirectory/roles.ini and icon-meanings.ini for
review without writing, renaming or deleting them. A subsequent explicit Save all choices stores
the prepared aggregate centrally. Legacy files remain intact. If the central route is missing,
Save is unavailable; the existing presentation fallback can still read local choices without
writing defaults. Assets, catalogs, Cortex cache paths and domain data remain outside the document.
Runtime presentation caches acknowledged central choices and refreshes on startup, editor reads,
picker opening and local saves; immediate external restore notifications remain host integration.

These mappings affect presentation only and never rename artwork or classify execution results.
The fixed development candidate/checks live under build/Plugins/SettingsPresets. Their selected
current Core/Desktop SDK closure is required; no Source activation or actual user migration is
part of the package fixtures. Unit/injected transport, native WPF behavior and actual SQLite/preset
checks are reported separately in chat-icons-handoff.json.

The picker uses a matched OS utility-window foreground/background palette, including high contrast.
Message icons use the host's live TextBrush through the existing DesktopIcons helper. Every icon
has a human/automation label; known native shapes need no external SVG renderer. Invalid catalog
data disables the picker with a diagnostic; embedded message decorations remain available.

## Canonical source and current verification

Build this directory's Cerebral.ChatIconsPlugin.csproj. Shared host/settings helpers remain linked
from examples/Shared; task staging projects and pinned reference imports are historical qualification
inputs, not the canonical build entry. Retain immutable packages and their original evidence.

The Root856 native Main check verifies insertion into an empty composer and between existing letters,
category/search filtering, Escape/Close dismissal and fresh reopen, with the complete saved draft file
restored. A later Configuration-only public update selects the reviewed 1.0.3 package and preserves
other Configuration packages and the Main catalog. In its rendered editor, changing Go Working to
Information, saving and closing/reopening persists the change; restoring Working, saving and explicit
Reload verifies the original value. This does not prove whole-database equality or an observed revision.

The normal canonical Release build passes with no warnings/errors and a 32-file output containing
no copied host SDK. Existing host references, including transitive Contracts, are explicit
`Private="false"` project references. New canonical build binaries are separately pinned and were not
substituted into the selected retained package. See [combined qualification](../../build/Core/OwnerQuiescence/ConversationProgress-sample-source/icons.root856.integration-qualified.json),
[earlier native picker evidence](../../build/Core/OwnerQuiescence/ConversationProgress-sample-source/icons.root856.native-check.json) and
[current completion conditions](../../docs/DEVELOPMENT-STATUS.md). Exact keyboard focus return, optional
Cortex assets and external application insertion remain separate.
The frozen 1.0.3 source/package join is HostLifetimeSuccessors/icons103.handoff.json under the existing
build/Plugins/SettingsPresets root; its prepared-only status describes that historical checkpoint.

## Reusable assets

The underlying platform-neutral source pack is assets/icons/chat-v1. It includes eleven SVG files,
index.ini for the existing native helper, catalog.ini for portable namespaced IDs, exact file hashes,
Licensing, schema/agent usage TXT documents and a small HTML example. Preferred installed shared
root is ProductPaths.SharedDirectory/Icons/chat-v1. Fallback roots are the product install's
Resources/Icons/chat-v1, this package's Resources/Icons/chat-v1, and the executable resource root.
Paths are derived at runtime; no Source machine path is hardcoded.

Stable identifier example: pegasus.chat-icons.status.success.
The compact tuple maps it to svg/ic_fluent_checkmark_circle_20_regular.svg plus asset/catalog/pack
hashes, source provenance and Licensing files. It is not a URI, Cortex file ID or access grant.
See the pack's AGENT-USAGE.txt and ASSET-PACK-SCHEMA.txt for exact create/read/validate/export steps.

The independent PackTool targets .NET 10 and has no Cerebral SDK dependency:
- validate <pack-root>
- export <pack-root> <absent-destination>
- export <pack-root> <absent-destination> <exact-key-or-alias>

Whole and individual exports preserve the immutable IDs, actual SVG bytes and required licenses.
Copies remain optional: agents should use the one shared immutable pack while authoring, then export
only the required assets to a project when it needs a distributable copy. The adapter itself does
not automatically install/import arbitrary third-party catalogs or grant paths. An optional Cortex
library tab uses the host's authenticated native resource commands for explicitly selected assets.

## Build, package and admission

Build this project in Release with an isolated artifacts path. Its built desktop-plugin.json and
adjacent Cerebral.ChatIconsPlugin.dll form the installable native package; Resources and Licensing
remain alongside them. The public Cerebral.Desktop.Plugins DLL is host-owned and is not privately
copied by the plugin. Existing Desktop API version 1 remains unchanged.

plugin-resources.json uses the existing reviewed shared-resource contract: source
Resources/Icons/chat-v1 -> ProductPaths.SharedDirectory/Icons/chat-v1. The existing schema 2
.CerebralPlugin export/prepare API carries this exact declaration, excludes those resource files
from the native desktop payload, and installs them once at the configured shared root. Owner review/import/enable is still required.
Root owns installer inclusion and live install. Install one shared asset pack under the configured
shared root and avoid per-agent resource libraries.

### Historical Source-install test lane

The following lane records the original 1.0.0 Source-only test setup. Its fixed task path and disabled
install are not the current 1.0.3 package update route. Normal management uses the selected catalog and
its public admitted install/update operations. Do not replay this historical lane as current acceptance.

The separate opt-in test executable selector --source-install <reviewed-manifest> accepts only
C:\Development\Pegasus-Cerebral\.tmp\icon-native-consumer-20261008\bin\Cerebral.ChatIconsPlugin\release\desktop-plugin.json.
It derives an explicit default Source binding through DesktopInstancePaths.ForSource, validates the
exact pegasus.chat-icons 1.0.0 native identity/entrypoint and local licensed pack, and exports a new
settings-free .CerebralPlugin under that task-owned artifacts directory. SDK preparation must retain
the full reviewed file fingerprint and exact single shared-resource declaration. SDK resource
Publish precedes a disabled catalog Install; native/resource integrity must match before Commit
and subsequent SetEnabled. Existing Chat Icons packages or retained generations reject. An existing
shared pack is accepted only if its complete file set and bytes already match the reviewed pack.
No portable settings are imported, no desktop window is opened, and no runtime reload is requested.
Output identifies the exported package, installed manifest/resource paths and integrity separately
from later plugin load/surface or picker verification. A failure prints phase evidence and never retries.

Root must verify the actual shared path is admitted READ ONLY through existing agent file/content
contracts. Discovery, a retained Cortex reference and this resolver do not grant access. No project
write or executable roots are broadened here; export requires an explicitly admitted destination.

## Optional Cortex library

The local picker remains the default. A host exposing IDesktopServiceHost adds a separate library
tab; opening that tab discovers up to eight enabled providers through cortex.assets.providers.
Only cortex-native-pipe providers are accepted. The user selects one explicitly. Search uses the
visible fixed knowledge.shared namespace and pegasus.assets/2 resource contract. Terms are bounded
to eight terms/256 characters, pages to four metadata results, and nextUri remains bound to the same
namespace, terms and limit with a maximum 256-character cursor. Query escapes and UTF-8 must be
well-formed; duplicate fields and unescaped separators reject locally. Cursor errors prompt a new
search; the adapter never bulk retries or hydrates a catalog.

Selecting one result reads exact cortex://<ref> metadata through cortex.assets.read with only
serverId and uri arguments. Metadata/envelopes are bounded to 32/128 KiB. Artwork is selected-only,
at most 2 MiB, requested in 64 KiB chunks (at most 32 requests). Every envelope URI, namespace, ID,
reference, media type, offset, length, total size, chunk digest and final full SHA-256 must match
before bytes can enter the optional plugin-owned cache. Cache entries bind provider/exact reference,
metadata digest and original content hash; at most sixteen entries/eight MiB are retained under
DataDirectory/cortex-icons-v2. Corrupt cache entries do not supply content; fresh exact reads remain
available with a cache diagnostic. Provider, search, selection, field lifetime and conversation
changes cancel obsolete operations. Opaque parameters stay uninterpreted and are never executed.

Only signature-checked PNG/JPEG with dimensions at most 2048px and four million pixels enter a
native bounded bitmap preview. Imported SVG remains original verified bytes with readable meaning,
license/source and exact resource references. Its visual preview explicitly requires a reviewed
renderer; content integrity is not renderer admission. The existing local 20px geometry pack keeps
its current native previews. No SVG renderer dependency, XAML execution or external asset fetch is added.

Insert text appears only when exact metadata supplies a bounded Unicode/text alternative; it uses
the existing conversation/field lifetime checks and never submits. Copy reference includes the
semantic meaning, content identity and license/provenance details reference. Cortex selected export
is disabled with a visible license-bundle-pending explanation. Original byte integrity and license
coordinates do not establish a complete required license/attribution bundle. No opaque parameters
are interpreted to invent that bundle. The local shared-pack export includes actual licenses/notices
and remains supported. The deterministic technical-copy helper only tests bytes/reference preservation;
its ZIP does not establish redistributability and has no enabled picker action.

Focused deterministic resource/cache/preview/export checks run with the existing test executable's
--cortex-only option. They use fake native resource envelopes and task-owned temporary files, and
do not exercise a live service/provider, user clipboard, model, installed app or deployment.

### Historical opt-in Source Cortex read lane

This separately prepared test lane documents its original Source owner binding. It is not current
installed-Main or optional-library acceptance and is not a prerequisite for the local icon picker.

The opt-in --source-cortex-live lane uses the real CerebralClient owner pipe declared only by the
default Source data root's Desktop/connection.json and verifies owner health before asset reads.
It bridges only the two native asset read commands through IDesktopServiceHost into the consumer,
then reads exact baseline/OpenMoji/Fluent references, validates metadata/chunk/full hashes, checks
semantic references, and round-trips selected real content through an owned temporary cache.
The baseline has an exact reference pin but no independently supplied size/hash tuple; imported
OpenMoji/Fluent samples additionally compare independently pinned sizes/hashes. Each reference's
proof prints before bounded discovery, so a later asset_busy search remains an explicit partial
result. There is no automatic retry. Owner health/declaration are rechecked on full completion.

Invoke the isolated test DLL with --source-cortex-live only after the owner has prepared Source.
This lane reads existing connection declaration and health; it does not start apps, change Source
files/settings/grants, use credential fields, call models/clipboard, or install anything. Temporary
cache files are removed. It reports native consumer reads separately from local cache verification.
SVG renderer admission, offscreen controls and installed picker interaction remain unverified by it.

## Licensing and evidence

Artwork is the existing Microsoft Fluent SYSTEM Icons subset pinned to
cd332ec1f90cda2fad16f86754f8da52f1e9ab30. Primary MIT license:
https://raw.githubusercontent.com/microsoft/fluentui-system-icons/cd332ec1f90cda2fad16f86754f8da52f1e9ab30/LICENSE
Required copyright/permission text and accompanying local notices are in Licensing.
No other candidate packs, fonts, npm packages, dependency installs or network fetch runtime ship.

Focused tests distinguish deterministic native controls/fake-host insertion from a live installed
chat test. They cover cursor/selection, stale entries, cancellation, host rejection, no submit,
role settings, exact package admission, theme/accessibility, catalog integrity/passive SVG limits
and whole/individual licensing-preserving export. Root owns later actual Chat/Configuration/MCP
read/renderer/clipboard/installer proof. No model inference or deployment is part of these fixtures.
