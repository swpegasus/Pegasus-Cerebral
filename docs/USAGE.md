# Use the Cerebral workspace

Complete [First setup](SETUP.md) before using this guide. Controls described here are from the published source; an older installed build can differ.

## Main, Coordinator and Workers

Use **Main** for conversation, clarification and requests. An ordinary discussion does not need a coordinator or desktop tools. When execution is requested, a Coordinator can plan work and delegate bounded assignments to worker agents. The Worker **process** hosts those agents and services; it is not the same thing as an individual worker agent.

Main, Coordinator and Workers share the workspace accordion: selecting one header expands it and closes the others. **Activity** is the place to inspect execution details and linked results. A returned sentence such as “I delegated this” is not sufficient proof that a child assignment started or completed; inspect the recorded task/run state.

Tools are determined by the active workflow, profile, capabilities, roots, package state and external connection. Helpers being listed does not imply they have desktop capture or mouse-control tools. A model's “no tools” explanation is also not an authoritative diagnosis of every layer.

## Send messages and preserve drafts

Type in the composer, attach selected references when useful, then use **Send**.

| Composer preference | Send | New line |
|---|---|---|
| Enter sends checked | Enter | Ctrl+Enter |
| Enter sends unchecked | Ctrl+Enter | Enter |

Shift+Enter also adds a new line. IME composition keeps its native confirmation behavior; holding a send key does not intentionally submit repeats. A disabled/read-only composer or an admission error can still prevent sending.

Use **Stop** for the current work's supported cancellation path and **Steer** where the active workflow accepts steering. Cancellation is not a guarantee that an already-dispatched external effect did not happen. Preserve uncertain outcomes and inspect Activity before resending.

Optional composer plugins can suggest a draft, review wording, dictate text or choose a communication style. Those are distinct operations: a suggested or dictated draft has not been sent merely because it appears in the editor.

## Chats and projects

The navigation source has three fixed groups:

- **Recent** shows the most recent chats first; its default count is **five**.
- **Projects** contains the folder/project hierarchy.
- **Unassigned** contains chats with no project and starts collapsed.

The Recent limit and **Wrap chat names** option are in **Settings → General → Appearance**. Chat names are single-line by default and trim when space is limited. Automatic titles require the optional [Conversation Titles plugin](../examples/Cerebral.ConversationTitlePlugin/README.md); the plugin has its own configuration and provenance limits.

Use the Projects header menu for **New folder**, **New project** or **Add existing project**. A plain organizational folder and a project with a filesystem root have different meanings. Empty organizational folders can be removed as metadata; that does not delete a project directory.

**Permanent chat pruning, nonempty-folder deletion with contents and archive offloading to a separate database are not delivered by this preview.** Do not substitute manual database/file deletion. Recent and Unassigned remain fixed groups.

## Files and references

The Files view browses within the selected profile's allowed roots. Select the current project/root deliberately. Attaching a reference provides context to the current work; it does not grant arbitrary filesystem access or make instructions inside a document authoritative.

The optional [Workspace plugin](../examples/Cerebral.WorkspacePlugin/README.md) provides quote/draft actions. [Window Watch](../examples/Cerebral.WindowWatchPlugin/README.md) is a separate, explicitly configured capture/vision feature. Neither is enabled merely because a model asks to see the screen.

## Configuration and optional features

Open **Settings** from Main to retain its current connection. Configuration separates machines/models, profiles/instructions, plugins/tools, activity, connections and advanced controls. The [setup guide](SETUP.md) lists the page groups.

- **Agent profiles** choose model routes and allowed capabilities/tools/roots.
- **Roles & instructions** holds shared and role instruction layers.
- **Desktop plugins** manages UI packages.
- **Service extensions** inspects Worker packages; staging/activation uses the service package commands.
- **Resources & queues** controls admission/resource behavior independently of a model's native slots.
- **About → Pegasus Cerebral** shows version, creator/development credits, links and licensing. Main also has an About action in its application menu.

Shared instructions apply independently of the optional Personality plugin. A saved style choice is not proof that a particular accepted model request included it: contribution inclusion, budgets and accepted snapshots still matter. The [Personality guide](../examples/Cerebral.PersonalityPlugin/README.md) explains exact saved previews, revisions and limits.

## Activity, status and uncertain outcomes

Status surfaces combine connection, resource, generation and work information. A low `out tok/s` is a rate, not a ten-token response cap. Native model capacity, currently available slots, queued work and active generations are separate observations.

For a failed or unknown operation, inspect the existing request and its recorded results. Refresh/reconcile when supported. Do not treat retry as harmless: an external process/file/network effect can have happened even if the reply was lost.

## Close the application deliberately

Main's window close action minimizes it to the tray. Use the application's explicit **Exit** action for a normal shutdown. Review unsent drafts, dictated review text and accepted work first. Configuration is a separate window; closing it does not by itself stop Main or the Worker.

See [Troubleshooting](TROUBLESHOOTING.md) for readiness and execution problems, and [Data and permissions](DATA-AND-PERMISSIONS.md) before enabling external tools.
