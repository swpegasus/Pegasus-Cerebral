Windows Dictation preference ownership

The package uses the existing authenticated desktop owner settings.plugins.get/save route for its global schema-1 preferences. The package validates its own closed JSON schema; Core stores the opaque document and preset transaction. The 128 KiB serialized-value bound remains unchanged. Vocabulary and correction lists above 250 entries are refused rather than truncated.

Opening settings only reads central state and hydrates a draft. Save preferences is explicit. Prepare existing local preferences reads the bounded legacy settings.json into a draft; it does not write, rename or delete that file. A selected retained document is admitted by the package before explicit save removes its exact pending ID. Unsupported active versions and unavailable storage disable save. Lost or malformed save acknowledgement requires a read and never causes automatic replay or legacy fallback.

The next listening session reads the acknowledged central preference snapshot. Portable export includes schemaVersion, minimumConfidence and voiceControlsEnabled. Recognizer identity, vocabulary and corrections remain local; complete local preset capture includes them. Audio, transcripts/test text, Windows device gain, speech profiles and training state are excluded. Presets do not enable the package or execute voice commands.

Current fixed private candidate/verification: build/Plugins/SettingsPresets/dictation-handoff.json and dictation-verification.json. Unit, offscreen native settings and actual SQLite/preset checks are separate evidence tiers. Installed package/Source adoption, microphone recognition and user migration are not proved by these fixtures.
