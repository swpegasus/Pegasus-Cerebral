using System.Text.Json;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

/// <summary>Configurator adapter over the shared renderer CAS session.</summary>
internal sealed class SettingsLayoutSession(Func<JsonElement> capture, Func<JsonElement?, Task> apply)
{
    private readonly RendererLayoutSession session = new("Integrator", IntegratorSettingsLayoutCodec.SchemaVersion,
        values => IntegratorSettingsLayoutCodec.Read(values), capture, apply,
        new(1, raw => IntegratorSettingsLayoutCodec.UpgradeValidatedV1ToV2(raw)));
    internal bool Hydrating => session.Hydrating;
    internal bool Dirty => session.Dirty;
    internal bool Blocked => session.Blocked;
    internal bool Supported => session.Supported;
    internal string Status => session.Status;
    internal void Changed() => session.Changed();
    internal Task LoadAsync(SettingsPresetClient owner, bool force, CancellationToken token) => session.LoadAsync(owner.LayoutOwner, force, token);
    internal Task FlushAsync(SettingsPresetClient owner, bool captureCurrent, CancellationToken token) => session.FlushAsync(owner.LayoutOwner, captureCurrent, token);
}

