using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Cerebral.Desktop.Plugins;

namespace Cerebral.AiDictationPlugin;

internal sealed record WindowsTextTargetSnapshot(string Identity, string Description, string Text, int Start, int Length);
internal interface IChosenWindowsTextTarget
{
    string Identity { get; }
    WindowsTextTargetSnapshot Read(CancellationToken token);
    void ReplaceSelection(WindowsTextTargetSnapshot expected, string text, CancellationToken token);
}

/// <summary>A single captured element. No enumeration, clipboard or keystrokes.</summary>
internal sealed class WindowsDictationTarget(IChosenWindowsTextTarget native, WindowsTextTargetSnapshot captured)
    : IDesktopTextEntry, IDesktopTextEntryLifetime, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private int busy;
    private bool uncertain;
    private bool disposed;
    internal bool Available => !disposed && !uncertain;
    internal IChosenWindowsTextTarget Native => native;
    private static readonly SemaphoreSlim NativeGate = new(1, 1);
    public string Id => captured.Identity;
    public string Purpose => captured.Description;
    public string Text => captured.Text;
    public int SelectionStart => captured.Start;
    public int SelectionLength => captured.Length;
    public CancellationToken AvailabilityToken => disposed ? new CancellationToken(true) : lifetime.Token;

    internal static Task<WindowsDictationTarget> GrabAsync(Point screenPoint, CancellationToken token) =>
        CaptureAsync(() => new WindowsUiaTextTarget(screenPoint), token);

    internal static async Task<WindowsDictationTarget> CaptureAsync(Func<IChosenWindowsTextTarget> choose, CancellationToken token)
    {
        // Only an explicit grabber release or Start reads one chosen control's text/selection.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var bounded = deadline.Token;
        var work = Task.Run(() =>
        {
            NativeGate.Wait(bounded);
            try
            {
                bounded.ThrowIfCancellationRequested();
                var picked = choose();
                var snapshot = picked.Read(bounded);
                bounded.ThrowIfCancellationRequested();
                return new WindowsDictationTarget(picked, snapshot);
            }
            finally { NativeGate.Release(); }
        }, bounded);
        var returned = false;
        try { var picked = await work.WaitAsync(bounded); returned = true; return picked; }
        finally
        {
            if (!returned)
                _ = work.ContinueWith(task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose();
                    else if (task.IsFaulted) _ = task.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    public async Task<DesktopOperationResult> InsertAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (disposed) return new(DesktopOperationState.Rejected, "The chosen destination was retired. Select it again.");
        if (uncertain) return new(DesktopOperationState.Uncertain, "The previous insertion is unconfirmed. Inspect the chosen app before selecting a destination again.");
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            return new(DesktopOperationState.Rejected, "The chosen destination is still processing the previous insertion.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        // Cooperative UI wait only: a synchronous UIA provider call may outlive this token.
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var bounded = deadline.Token;
        var dispatched = false;
        var work = Task.Run(() =>
        {
            var nativeGate = false;
            try
            {
                if (native is WindowsUiaTextTarget) { NativeGate.Wait(bounded); nativeGate = true; }
                var actual = native.Read(bounded);
                if (actual.Identity != captured.Identity || actual.Text != captured.Text || actual.Start != captured.Start || actual.Length != captured.Length)
                    return new DesktopOperationResult(DesktopOperationState.Rejected,
                        "The chosen text box or selection changed. Your transcript is retained; select the destination again.");
                bounded.ThrowIfCancellationRequested();
                dispatched = true;
                native.ReplaceSelection(captured, text, bounded);
                var inserted = native.Read(bounded);
                var expectedText = captured.Text[..captured.Start] + text + captured.Text[(captured.Start + captured.Length)..];
                if (inserted.Identity != captured.Identity || inserted.Text != expectedText ||
                    inserted.Start != captured.Start + text.Length || inserted.Length != 0)
                    throw new InvalidOperationException("The chosen app did not confirm the inserted text and cursor.");
                captured = inserted;
                return new DesktopOperationResult(DesktopOperationState.Completed, "Inserted into the explicitly chosen text box. Nothing was submitted.");
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (dispatched) uncertain = true;
                return new DesktopOperationResult(dispatched ? DesktopOperationState.Uncertain : DesktopOperationState.Rejected,
                    dispatched ? "Insertion could not be confirmed. Inspect the chosen app before another insertion." : error.Message);
            }
            finally { if (nativeGate) NativeGate.Release(); Interlocked.Exchange(ref busy, 0); }
        });
        try { return await work.WaitAsync(bounded); }
        catch (OperationCanceledException)
        {
            // A timed-out write may already have taken effect. Retire this lease, never replay it.
            uncertain = true;
            return new(DesktopOperationState.Uncertain, "The destination did not confirm in time. Inspect it before selecting a destination again.");
        }
    }

    public void Dispose() { if (disposed) return; disposed = true; lifetime.Cancel(); lifetime.Dispose(); }
}

/// <summary>Strict supported subset: same-user non-elevated editable Value+Text providers with a provable single selection.</summary>
internal sealed class WindowsUiaTextTarget : IChosenWindowsTextTarget
{
    private readonly AutomationElement element;
    private readonly int processId;
    private readonly long processStarted;
    private readonly int[] runtimeId;
    private readonly nint root;
    private const int Limit = DictationReview.MaximumCharacters;
    internal long ObservedAt { get; }
    public string Identity => $"windows:{processId}:{processStarted}:{root}:{string.Join('.', runtimeId)}";

    internal WindowsUiaTextTarget(Point point) : this(AutomationElement.FromPoint(point)) { }

    internal WindowsUiaTextTarget(AutomationElement candidate, long observedAt = 0)
    {
        ObservedAt = observedAt;
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Windows text-box picking is unavailable on this platform.");
        // Only the pointed element and its bounded ancestor chain are inspected, never windows/apps enumerated.
        for (var depth = 0; depth < 8 && candidate is not null; depth++)
        {
            if (candidate.Current.IsPassword) throw new InvalidOperationException("Password and protected text boxes cannot be dictation destinations.");
            if (candidate.Current.ControlType == ControlType.Document || candidate.Current.ClassName.Contains("RichEdit", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Rich document editors are not supported by this text-box insertion bridge. Choose a plain editable text box.");
            if (candidate.Current.ControlType == ControlType.Edit)
            {
                element = candidate; break;
            }
            candidate = TreeWalker.ControlViewWalker.GetParent(candidate);
        }
        if (element is null) throw new InvalidOperationException("Choose a supported editable text box. This control is not supported.");
        processId = element.Current.ProcessId;
        using var process = Process.GetProcessById(processId);
        using var self = Process.GetCurrentProcess();
        if (process.Id == self.Id)
            throw new InvalidOperationException("Use Settings > Use the last chosen Cerebral editor for this app. The dictation review itself cannot be a destination.");
        if (process.SessionId != self.SessionId || !WindowsTargetAccess.SameUserUnelevated(process))
            throw new InvalidOperationException("Choose a text box in an ordinary app running as the same signed-in user. Protected or elevated apps are not supported.");
        processStarted = process.StartTime.ToUniversalTime().Ticks;
        runtimeId = element.GetRuntimeId();
        if (runtimeId.Length is < 1 or > 64) throw new InvalidOperationException("The chosen control does not expose a stable identity.");
        candidate = element;
        for (var depth = 0; depth < 12 && candidate is not null; depth++)
        {
            if (candidate.Current.NativeWindowHandle != 0) { root = GetAncestor(candidate.Current.NativeWindowHandle, 2); break; }
            candidate = TreeWalker.ControlViewWalker.GetParent(candidate);
        }
        if (root == 0) throw new InvalidOperationException("The chosen text box has no supported stable window identity.");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var writable) || writable is not ValuePattern value || value.Current.IsReadOnly ||
            !element.TryGetCurrentPattern(TextPattern.Pattern, out var selectable) || selectable is not TextPattern text || text.SupportedTextSelection != SupportedTextSelection.Single)
            throw new InvalidOperationException("Choose a plain editable text box with a supported cursor selection.");
    }

    public WindowsTextTargetSnapshot Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ValidateIdentity();
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject) || valueObject is not ValuePattern value || value.Current.IsReadOnly ||
            !element.TryGetCurrentPattern(TextPattern.Pattern, out var textObject) || textObject is not TextPattern text || text.SupportedTextSelection != SupportedTextSelection.Single)
            throw new InvalidOperationException("This text box does not expose supported editable text and a single stable selection. Choose another supported control.");
        var body = text.DocumentRange.GetText(Limit + 1);
        if (body.Length > Limit) throw new InvalidOperationException("This text box is too large for safe dictation insertion.");
        var content = value.Current.Value;
        if (content.Length > Limit || body != content) throw new InvalidOperationException("This app does not expose a consistent bounded text/selection contract.");
        var selections = text.GetSelection();
        if (selections.Length != 1) throw new InvalidOperationException("Choose one insertion point or text selection in the destination.");
        var prefix = text.DocumentRange.Clone();
        prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, selections[0], TextPatternRangeEndpoint.Start);
        var start = prefix.GetText(Limit + 1).Length;
        var selected = selections[0].GetText(Limit + 1);
        if (start > content.Length || selected.Length > content.Length - start || content.Substring(start, selected.Length) != selected)
            throw new InvalidOperationException("The destination's selection could not be verified.");
        token.ThrowIfCancellationRequested();
        return new(Identity,
            $"{ProcessName()} · {SafeLabel(element.Current.Name)}", content, start, selected.Length);
    }

    public void ReplaceSelection(WindowsTextTargetSnapshot expected, string insertion, CancellationToken token)
    {
        var actual = Read(token);
        if (actual.Identity != expected.Identity || actual.Text != expected.Text || actual.Start != expected.Start || actual.Length != expected.Length)
            throw new InvalidOperationException("The selected destination changed before insertion.");
        var replacement = expected.Text[..expected.Start] + insertion + expected.Text[(expected.Start + expected.Length)..];
        if (replacement.Length > Limit) throw new InvalidOperationException("The resulting destination text would exceed the supported bound.");
        token.ThrowIfCancellationRequested(); ValidateIdentity();
        var value = (ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern);
        token.ThrowIfCancellationRequested(); // The preceding native/provider lookups can block.
        value.SetValue(replacement);
        if (value.Current.Value != replacement) throw new InvalidOperationException("The chosen app did not confirm the inserted text.");
        var text = (TextPattern)element.GetCurrentPattern(TextPattern.Pattern);
        var caret = text.DocumentRange.Clone();
        caret.MoveEndpointByRange(TextPatternRangeEndpoint.End, caret, TextPatternRangeEndpoint.Start);
        var prefixText = replacement[..(expected.Start + insertion.Length)];
        if (prefixText.Length > 0)
        {
            // UIA character units can differ from UTF-16 string offsets. Locate the exact
            // known prefix instead of guessing offsets around emoji or combining characters.
            var prefix = text.DocumentRange.FindText(prefixText, false, false)
                ?? throw new InvalidOperationException("The chosen app did not confirm the resulting cursor position.");
            if (prefix.CompareEndpoints(TextPatternRangeEndpoint.Start, text.DocumentRange, TextPatternRangeEndpoint.Start) != 0)
                throw new InvalidOperationException("The resulting cursor prefix could not be verified.");
            caret.MoveEndpointByRange(TextPatternRangeEndpoint.Start, prefix, TextPatternRangeEndpoint.End);
            caret.MoveEndpointByRange(TextPatternRangeEndpoint.End, prefix, TextPatternRangeEndpoint.End);
        }
        caret.Select();
    }

    private void ValidateIdentity()
    {
        if (!IsWindow(root) || element.Current.IsPassword || !element.Current.IsEnabled || element.Current.ControlType != ControlType.Edit ||
            element.Current.ClassName.Contains("RichEdit", StringComparison.OrdinalIgnoreCase) || element.Current.ProcessId != processId ||
            !element.GetRuntimeId().SequenceEqual(runtimeId)) throw new InvalidOperationException("The chosen text box closed, changed or became protected. Select it again.");
        GetWindowThreadProcessId(root, out var owner);
        using var process = Process.GetProcessById(processId);
        if (owner != processId || process.StartTime.ToUniversalTime().Ticks != processStarted || !WindowsTargetAccess.SameUserUnelevated(process))
            throw new InvalidOperationException("The chosen app identity or permission changed. Select a supported destination again.");
    }
    private string ProcessName() { using var process = Process.GetProcessById(processId); return process.ProcessName; }
    private static string SafeLabel(string value) => string.IsNullOrWhiteSpace(value) ? "selected text box" : new(value.Where(c => !char.IsControl(c)).Take(120).ToArray());
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint handle, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out int processId);
}

internal static class WindowsTargetAccess
{
    internal static bool SameUserUnelevated(Process process)
    {
        nint token = 0, buffer = 0;
        try
        {
            if (!OpenProcessToken(process.Handle, 8, out token)) return false;
            buffer = Marshal.AllocHGlobal(4);
            if (!GetTokenInformation(token, 20, buffer, 4, out _) || Marshal.ReadInt32(buffer) != 0) return false;
            Marshal.FreeHGlobal(buffer); buffer = 0;
            GetTokenInformation(token, 1, 0, 0, out var bytes);
            if (bytes is < 8 or > 65536) return false;
            buffer = Marshal.AllocHGlobal(bytes);
            if (!GetTokenInformation(token, 1, buffer, bytes, out _)) return false;
            using var current = WindowsIdentity.GetCurrent();
            return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value == current.User?.Value;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
        finally { if (buffer != 0) Marshal.FreeHGlobal(buffer); if (token != 0) CloseHandle(token); }
    }
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(nint token, int kind, nint buffer, int length, out int required);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}
