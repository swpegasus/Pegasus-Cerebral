# Pegasus AutoPrompt

Optional next-message suggestions for Cerebral's Main chat. A one-off request receives only the latest mounted public final response and the plugin's fixed policy. Activity, tool output, other chats, history, and agent instructions are excluded.

Suggestions appear as ghost text only when the actual Main composer is visible and empty. **Tab inserts; it never sends.** Typing, changing chats or owners, hiding Main, or receiving a newer response invalidates pending suggestions. IME and ordinary keyboard navigation remain available.

The plugin is disabled by default. Select an explicit AutoPrompt profile and revision in its settings. Generation requires the matching host capability and an already loaded compatible local model; it obeys the engine's maintenance Hold and resource admission. It does not create profiles, load or switch models, inherit tool grants, or fall back to direct inference.

Build and release this package with its matching framework changes. It is not a standalone update for the older installed desktop SDK. Fake-backend and offscreen-control tests do not establish live model or visual acceptance.
