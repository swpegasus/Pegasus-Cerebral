using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cerebral.Client;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private readonly TextBlock engineSummary = Ui.Text("Connect and refresh to inspect the current engine.", 14);
    private readonly ComboBox engineSlot = new() { ItemsSource = new[] { "A", "B" }, SelectedIndex = 1, Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock engineActionHelp = Ui.Text("Refresh the engine status to see available actions.", 12, Ui.Muted);
    private Button? enginePause, engineResume, engineActivate, engineRollback, enginePromote;
    private EngineActionState? engineActionState;
    private CerebralClient? engineStatusClient;
    private long? engineEpoch;
    private long engineRefreshVersion;

    private sealed record EngineActionState(bool Paused, bool Healthy, bool? ResumeHoldBlocked, string? ActiveSlot, bool CandidatePending,
        string? KnownGoodSlot, int ActiveEvaluations, long CommittedDecisions, int UnreleasedContexts,
        IReadOnlyDictionary<string, bool> AvailableSlots, IReadOnlyDictionary<string, bool> InactiveSlots);

    private static bool? HoldBlocksResume(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("contract", out var contract) || contract.ValueKind != JsonValueKind.String ||
            contract.GetString() != "cerebral.owner-work-hold/1" ||
            !data.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String ||
            !data.TryGetProperty("holdId", out var holdId)) return null;
        var name = state.GetString();
        if (name == "none") return holdId.ValueKind == JsonValueKind.Null ? false : null;
        if (holdId.ValueKind != JsonValueKind.String || !Guid.TryParseExact(holdId.GetString(), "N", out _)) return null;
        return name switch
        {
            "holding" or "held" or "resuming" or "cancelling" or "faulted" => true,
            "resumed" or "cancelled" => false,
            _ => null
        };
    }

    /// <summary>Actual owner-pipe and WPF layout inspection. Never shows a window or changes engine selection.</summary>
    internal async Task CheckEnginePageAsync(string capturePath)
    {
        Dispatcher.VerifyAccess();
        var saved = ConnectionStore.Read().Preferences;
        if (saved?.Transport != "pipe" || saved.LocalEndpoint is null || string.IsNullOrWhiteSpace(saved.LocalDataDirectory))
            throw new InvalidOperationException("Engine UI inspection requires an existing local owner pipe.");
        await InspectServiceUiAsync(saved.LocalEndpoint, saved.LocalDataDirectory, null);
        SelectTab("Engine");
        var systemTabs = (TabControl)((TabItem)Tabs.SelectedItem).Content;
        await RefreshEngine();
        if (!IsConnected || engineEpoch is null || !engineSummary.Text.Contains("activation " + engineEpoch, StringComparison.Ordinal))
            throw new InvalidOperationException("The Engine page did not render a verified owner status.");
        var page = (FrameworkElement)((TabItem)systemTabs.SelectedItem).Content;
        // An unshown Window has no presentation source; arrange its real content tree directly.
        var layout = (FrameworkElement)Content;
        layout.Measure(new Size(1360, 880)); layout.Arrange(new Rect(0, 0, 1360, 880)); layout.UpdateLayout();
        if (page.ActualWidth <= 0 || page.ActualHeight <= 0 || engineSummary.ActualWidth <= 0 || engineSummary.ActualHeight <= 0)
            throw new InvalidOperationException("The selected Engine page did not obtain a visible layout.");
        var image = new RenderTargetBitmap(1360, 880, 96, 96, PixelFormats.Pbgra32);
        image.Render(layout);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var file = new FileStream(Path.GetFullPath(capturePath), FileMode.CreateNew, FileAccess.Write)) encoder.Save(file);
        Console.WriteLine(JsonSerializer.Serialize(new { success = true, connected = IsConnected, epoch = engineEpoch,
            summary = engineSummary.Text, capture = Path.GetFullPath(capturePath), windowShown = false,
            engineMutationsExecuted = false, persistentWindowPreferencesWritten = false }));
    }

    private UIElement EnginePage()
    {
        var body = new StackPanel { Margin = new Thickness(8) };
        body.Children.Add(engineSummary);
        body.Children.Add(Ui.Text("Candidate slot", 13));
        body.Children.Add(engineSlot);
        body.Children.Add(engineActionHelp);
        enginePause = Ui.Button("Pause", () => Run(() => ChangeEngine("engine.pause")));
        engineResume = Ui.Button("Resume", () => Run(() => ChangeEngine("engine.resume")));
        engineActivate = Ui.Button("Activate selected", () => Run(() => ChangeEngine("engine.activate")));
        engineRollback = Ui.Button("Roll back", () => Run(() => ChangeEngine("engine.rollback")));
        enginePromote = Ui.Button("Confirm tested candidate", () => Run(() => ChangeEngine("engine.promote")));
        var page = Ui.Page("Workflow decision engine", "Pause at a safe boundary, activate an inactive candidate, resume and test it. Keep the previous engine until you confirm the candidate. Chat, drafts, feature plugins and host receipts remain in place.", body,
            Ui.Button("Refresh", () => Run(RefreshEngine)), enginePause,
            engineResume, engineActivate, engineRollback, enginePromote);
        engineSlot.SelectionChanged += (_, _) => UpdateEngineActions();
        ServiceStatusChanged += (_, _) => UpdateEngineActions();
        UpdateEngineActions();
        page.IsVisibleChanged += (_, _) => { if (page.IsVisible && IsConnected) Run(RefreshEngine); };
        return page;
    }

    private void UpdateEngineActions()
    {
        // ConnectCore replaces the client even when the saved endpoint is unchanged.
        // A prior connection's epoch must never re-enable controls during reconnect.
        if (engineStatusClient is not null && (!IsConnected || !ReferenceEquals(Client, engineStatusClient)))
        {
            engineStatusClient = null;
            engineActionState = null;
            engineEpoch = null;
        }
        var state = engineActionState;
        var selected = engineSlot.SelectedItem as string;
        var selectedAvailable = selected is not null && state is not null &&
            state.AvailableSlots.TryGetValue(selected, out var available) && available;
        var selectedInactive = selected is not null && state is not null &&
            state.InactiveSlots.TryGetValue(selected, out var inactive) && inactive;
        var quiescent = state is { Paused: true, ActiveEvaluations: 0, UnreleasedContexts: < 32 };
        if (enginePause is not null) enginePause.IsEnabled = IsConnected && state is { Paused: false };
        if (engineResume is not null) engineResume.IsEnabled = IsConnected && state is { Paused: true, Healthy: true, ResumeHoldBlocked: false };
        if (engineActivate is not null) engineActivate.IsEnabled = IsConnected && quiescent &&
            state is { CandidatePending: false } && selectedAvailable && selectedInactive &&
            (state.ActiveSlot is null || selected != state.ActiveSlot);
        if (engineRollback is not null) engineRollback.IsEnabled = IsConnected && quiescent &&
            state is { CandidatePending: true, KnownGoodSlot: not null };
        if (enginePromote is not null) enginePromote.IsEnabled = IsConnected &&
            state is { CandidatePending: true, Healthy: true, CommittedDecisions: > 0 };
        engineActionHelp.Text = state switch
        {
            null => "Refresh the engine status to see available actions.",
            { CandidatePending: false } when selectedAvailable && !selectedInactive => "The selected slot is current or protected. Choose a verified inactive slot before activation.",
            { ActiveSlot: null, CandidatePending: false } when selectedAvailable && selectedInactive => "No engine is active. A verified inactive slot can be activated while paused to recover.",
            { CandidatePending: false } when !selectedAvailable => $"Slot {selected ?? "selection"} is unavailable. Choose a verified inactive slot and pause before activating it. Rollback and confirmation require an unconfirmed candidate.",
            { Healthy: false } => "The active engine reports an error. Inspect its status before resuming or confirming it.",
            { CandidatePending: false } when selected == state?.ActiveSlot => "The selected slot is already active. Choose a verified inactive slot after pausing.",
            { Paused: false, CandidatePending: false } => "Pause the engine before switching slots. Rollback and confirmation require an unconfirmed candidate.",
            { Paused: false, CandidatePending: true } => "Pause before rolling back. Confirm only after a host-committed candidate decision.",
            { CandidatePending: false } => "Activate a verified inactive slot while paused. Rollback and confirmation require an unconfirmed candidate.",
            { ActiveEvaluations: > 0 } => "Wait for active evaluations to finish before switching or rolling back.",
            { UnreleasedContexts: >= 32 } => "Engine contexts must be released before another switch or rollback.",
            { CommittedDecisions: 0 } => "Test the candidate with a committed decision before confirming it.",
            _ => "Review the available candidate actions and their current status above."
        };
        if (state is { Paused: true, ResumeHoldBlocked: true })
            engineActionHelp.Text += " An intentional work hold blocks Resume; resolve its owner marker first.";
        else if (state is { Paused: true, ResumeHoldBlocked: null })
            engineActionHelp.Text += " Work-hold status is unavailable, so Resume stays disabled.";
    }

    private async Task RefreshEngine()
    {
        var version = ++engineRefreshVersion;
        if (!IsConnected) { engineEpoch = null; engineActionState = null; engineStatusClient = null; engineSummary.Text = "Connect and refresh to inspect the current engine."; UpdateEngineActions(); return; }
        engineEpoch = null;
        engineActionState = null;
        engineStatusClient = null;
        engineSummary.Text = "Refreshing engine status…";
        UpdateEngineActions();
        var captured = Client;
        var result = await captured.CommandAsync("engine.status", cancellationToken: Lifetime.Token);
        if (version != engineRefreshVersion || !IsConnected || !ReferenceEquals(Client, captured)) return;
        if (!result.Success || result.Data.ValueKind != JsonValueKind.Object) throw new InvalidOperationException(result.Error ?? "Engine status was not returned.");
        bool? holdBlocksResume = null;
        try
        {
            var hold = await captured.CommandAsync("engine.work-hold.status", cancellationToken: Lifetime.Token);
            if (hold.Success) holdBlocksResume = HoldBlocksResume(hold.Data);
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { /* A bounded prior read left the hold state unknown. */ }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.Net.Http.HttpRequestException or JsonException)
        { /* Unknown hold state keeps Resume disabled; engine status remains inspectable. */ }
        if (version != engineRefreshVersion || !IsConnected || !ReferenceEquals(Client, captured)) return;
        var data = result.Data;
        engineEpoch = data.GetProperty("epoch").GetInt64();
        var active = data.GetProperty("active");
        var name = active.ValueKind == JsonValueKind.Null ? "Unavailable" : $"{active.GetProperty("id").GetString()} {active.GetProperty("version").GetString()} · slot {active.GetProperty("slot").GetString()}";
        var lines = new List<string> { name, $"{(data.GetProperty("paused").GetBoolean() ? "Paused" : "Running")} · activation {engineEpoch} · active evaluations {data.GetProperty("activeEvaluations").GetInt32()} · committed decisions {data.GetProperty("committedDecisions").GetInt64()}",
            data.GetProperty("candidatePending").GetBoolean() ? "Candidate awaiting your test and confirmation; the known-good fallback is protected." : "No unconfirmed candidate." };
        foreach (var slot in data.GetProperty("slots").EnumerateArray())
            lines.Add($"Slot {slot.GetProperty("slot").GetString()}: {(slot.GetProperty("available").GetBoolean() ? "package verified" : "unavailable")} · {(slot.GetProperty("buildAllowed").GetBoolean() ? "available for a build" : "protected")}");
        if (data.GetProperty("error").ValueKind == JsonValueKind.String) lines.Add(data.GetProperty("error").GetString()!);
        if (data.TryGetProperty("recovery", out var recovery) && recovery.ValueKind == JsonValueKind.String) lines.Add(recovery.GetString()!);
        lines.Add(holdBlocksResume switch { true => "Intentional work hold blocks Resume.", false => "No active intentional work hold.", _ => "Intentional work-hold status unavailable; Resume disabled." });
        engineSummary.Text = string.Join(Environment.NewLine, lines);
        engineActionState = new(data.GetProperty("paused").GetBoolean(), active.ValueKind != JsonValueKind.Null &&
            data.GetProperty("error").ValueKind == JsonValueKind.Null,
            holdBlocksResume,
            active.ValueKind == JsonValueKind.Null ? null : active.GetProperty("slot").GetString(),
            data.GetProperty("candidatePending").GetBoolean(),
            data.GetProperty("knownGoodSlot").ValueKind == JsonValueKind.Null ? null : data.GetProperty("knownGoodSlot").GetString(),
            data.GetProperty("activeEvaluations").GetInt32(), data.GetProperty("committedDecisions").GetInt64(),
            data.GetProperty("unreleasedContexts").GetInt32(),
            data.GetProperty("slots").EnumerateArray().ToDictionary(slot => slot.GetProperty("slot").GetString()!,
                slot => slot.GetProperty("available").GetBoolean(), StringComparer.Ordinal),
            data.GetProperty("slots").EnumerateArray().ToDictionary(slot => slot.GetProperty("slot").GetString()!,
                slot => slot.GetProperty("buildAllowed").GetBoolean(), StringComparer.Ordinal));
        engineStatusClient = captured;
        UpdateEngineActions();
    }

    private async Task ChangeEngine(string operation)
    {
        if (!IsConnected) throw new InvalidOperationException("Connect to a Worker first.");
        if (engineEpoch is null) await RefreshEngine();
        if (operation == "engine.promote" && !Confirm("Confirm that this candidate has passed your functional test? Its previous slot will then become available for the next build.")) return;
        var arguments = operation == "engine.activate" ? (object)new { slot = engineSlot.SelectedItem?.ToString(), expectedEpoch = engineEpoch }
            : new { expectedEpoch = engineEpoch };
        var result = await Client.CommandAsync(operation, arguments, cancellationToken: Lifetime.Token);
        await RefreshEngine();
        if (!result.Success) throw new InvalidOperationException(result.Error ?? "Engine operation failed.");
    }
}
