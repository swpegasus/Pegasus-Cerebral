using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Cerebral.Desktop;

public static class WindowPreferenceStore
{
    private static readonly object Gate = new();
    private static string DirectoryPath => DesktopPreferenceDiagnostics.UiDirectory;

    public static void Attach(Window window, string application, TabControl tabs)
    {
        var path = Path.Combine(DirectoryPath, SafeName(application) + ".window.json");
        RestoreGeometry(window, Read<WindowState>(path), tabs);
        var timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(450) };
        timer.Tick += (_, _) => { timer.Stop(); Save(); };
        void Changed(object? _, EventArgs __) { timer.Stop(); timer.Start(); }
        window.LocationChanged += Changed; window.SizeChanged += Changed; window.StateChanged += Changed;
        tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.OriginalSource, tabs)) Changed(null, EventArgs.Empty); };
        window.Closing += (_, _) => { timer.Stop(); Save(); };
        void Save()
        {
            var bounds = window.WindowState == System.Windows.WindowState.Normal ? new Rect(window.Left, window.Top, window.Width, window.Height) : window.RestoreBounds;
            if (!double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top) ||
                !double.IsFinite(bounds.Width) || bounds.Width < 400 || !double.IsFinite(bounds.Height) || bounds.Height < 300) return;
            Write(path, new WindowState(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                window.WindowState == System.Windows.WindowState.Maximized, Math.Max(0, tabs.SelectedIndex)));
        }
    }

    /// <summary>Hydrates legacy geometry without attaching a writer or restoring the legacy tab index.</summary>
    public static bool RestoreLegacyGeometry(Window window, string application)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(application);
        window.Dispatcher.VerifyAccess();
        var path = Path.Combine(DirectoryPath, SafeName(application) + ".window.json");
        return RestoreGeometry(window, Read<WindowState>(path), legacyTabs: null);
    }

    private static bool RestoreGeometry(Window window, WindowState? saved, TabControl? legacyTabs)
    {
        if (saved is null || !double.IsFinite(saved.Width) || saved.Width < 400 ||
            !double.IsFinite(saved.Height) || saved.Height < 300 ||
            !double.IsFinite(saved.Left) || !double.IsFinite(saved.Top)) return false;
        window.Width = saved.Width; window.Height = saved.Height;
        window.Left = saved.Left; window.Top = saved.Top; window.WindowStartupLocation = WindowStartupLocation.Manual;
        if (saved.Maximized)
        {
            if (legacyTabs is null && window.IsLoaded) window.WindowState = System.Windows.WindowState.Maximized;
            else window.Loaded += (_, _) => window.WindowState = System.Windows.WindowState.Maximized;
        }
        if (legacyTabs is not null)
            legacyTabs.Loaded += (_, _) => legacyTabs.SelectedIndex = Math.Clamp(saved.TabIndex, 0, Math.Max(0, legacyTabs.Items.Count - 1));
        return true;
    }

    private static string SafeName(string value) => string.Concat(value.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_'));
    private static T? Read<T>(string path)
    {
        lock (Gate)
        {
            try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return default; }
        }
    }
    private static void Write<T>(string path, T value)
    {
        lock (Gate)
        {
            var temporary = path + ".new";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(temporary, JsonSerializer.Serialize(value));
                File.Move(temporary, path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
    private sealed record WindowState(double Left, double Top, double Width, double Height, bool Maximized, int TabIndex);
}

public static class UiPreferenceStore
{
    private static readonly object Gate = new();
    private static string PathName => Path.Combine(DesktopPreferenceDiagnostics.UiDirectory, "preferences.json");
    private static string StringPathName => Path.Combine(DesktopPreferenceDiagnostics.UiDirectory, "string-preferences.json");

    public static bool ReadBool(string key, bool fallback)
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(PathName)) return fallback;
                var values = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(PathName));
                return values?.GetValueOrDefault(key, fallback) ?? fallback;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return fallback; }
        }
    }

    public static void WriteBool(string key, bool value)
    {
        lock (Gate)
        {
            Dictionary<string, bool> values;
            try { values = File.Exists(PathName) ? JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(PathName)) ?? [] : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { values = []; }
            values[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            var temporary = PathName + ".new";
            File.WriteAllText(temporary, JsonSerializer.Serialize(values));
            File.Move(temporary, PathName, true);
        }
    }

    public static string? ReadString(string key)
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(StringPathName)) return null;
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StringPathName))?.GetValueOrDefault(key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
        }
    }

    public static void WriteString(string key, string value)
    {
        lock (Gate)
        {
            Dictionary<string, string> values;
            try { values = File.Exists(StringPathName) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StringPathName)) ?? [] : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { values = []; }
            values[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(StringPathName)!);
            var temporary = StringPathName + ".new";
            File.WriteAllText(temporary, JsonSerializer.Serialize(values));
            File.Move(temporary, StringPathName, true);
        }
    }
}
