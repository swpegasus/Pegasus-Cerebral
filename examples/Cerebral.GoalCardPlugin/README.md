# Conversation Goal desktop plugin

This optional bundled plugin reads the original accepted request and the latest eight later user messages or corrections from the conversation journal. It does not synthesize an objective, add model context, store another copy of the conversation, or submit work.

Expand the Goal card above the composer, or use the Goal workspace action while a question is pending. Large requests are visibly marked as excerpts; earlier messages remain in chat history. Add goal correction appends an editable prefix to the exact current draft through the existing binding/revision guard. The user chooses whether to send or steer it.

The package uses the existing composer-accessory and workspace-action mounts, the stable desktop SDK, and the owner-only `conversations.goal` journal projection. Panels and composer actions are empty. It performs no inference, periodic polling, or filesystem writes. Disabling the package removes only its presentation.
