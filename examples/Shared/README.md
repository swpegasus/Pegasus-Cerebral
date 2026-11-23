# Shared native-plugin source helpers

This directory is **source-link support**, not an installable plugin or separate SDK assembly. It has no project, manifest, independent version, service registration or standalone build. Consuming projects select the needed files explicitly and compile them into their own assemblies.

Build each consumer using its README and [Build guide](../../docs/BUILD.md). Preserve this relative `examples/Shared` directory in source checkouts; copying only a consumer folder can omit declared compile inputs. Do not deploy these `.cs` files as an extra package or copy replacement host DLLs into plugins.

## Files and boundaries

| File | Purpose |
| --- | --- |
| [PluginHostAccess.cs](PluginHostAccess.cs) | Retired-host notification/revalidation and cleanup retaining ordinary failures. Handles disposal at owned callback boundaries, not broad error suppression. |
| [DictatedTextInsertion.cs](DictatedTextInsertion.cs) | Captures exact text/selection, checks it still matches, and prepares insertion preserving selected text with punctuation-aware spacing. Neither records audio nor sends chat. |
| [PackageSettingsOwnerClient.cs](PackageSettingsOwnerClient.cs) | Package-scoped versioned settings calls, bounded closed responses and revision/correlation checks; complete-transfer envelopes only when advertised. |
| [PackagePreferenceJson.cs](PackagePreferenceJson.cs) | JSON/policy helpers for a consumer's preference contract. |
| [PackagePreferenceSession.cs](PackagePreferenceSession.cs) | Loaded baseline, acknowledged values, retained-document admission and exact-revision save lifecycle. |
| [PackagePreferenceEditor.cs](PackagePreferenceEditor.cs) | Explicit load/prepare/review/save WPF workflow for preference panels. |

The host owns storage, transfer purpose and authorization. The package owns ID/version, defaults, schema, policy, projections and path meaning. These helpers do not grant host access or replace host-side owner authentication/admission.

## Preference workflow

Opening an editor reads central settings, without saving or automatically reading legacy files. **Load saved preferences** establishes a baseline. Where a package supplies it, **Prepare existing local preferences** fills a reviewable draft. Retained documents can be prepared explicitly. **Save preferences** applies the reviewed draft against the loaded revision.

Preparation alone does not activate models, start recording, import unknown documents or overwrite central state. Runtime behavior uses acknowledged values. After a failed or uncertain save, reload before another change; do not claim unconfirmed success. Unsupported documents remain preserved. Portable transfers are not interchangeable with complete transfers containing local references.

## Privacy and troubleshooting

These source files contain no user preferences, recordings, credentials or model data. Consumers can handle sensitive settings and paths; this layer is not universal encryption or redaction. Keep runtime stores, pending imports, legacy files and diagnostics out of Git/public packages. Host user settings are separate from this source directory.

Missing `IDesktopServiceHost`, unsupported contracts and inconsistent revisions are real unavailable states. Use a compatible host and reload via the consumer panel rather than adding a raw file or shell fallback. Dictation insertion failures should preserve existing text and permit review in the current field; helpers do not authorize resending or restoring stale selection automatically.

No external service, key, weights or separate runtime is needed by the helpers themselves. Consumers may add requirements in their own READMEs. First-party source follows the repository [LICENSE](../../LICENSE). See [Plugin installation](../../docs/PLUGINS.md) and [Setup](../../docs/SETUP.md). This describes source behavior, not build, audio-device or live settings-service verification.
