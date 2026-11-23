using System.IO;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Isolated file-store checks for --check-ui; never opens a provider or changes the user's connection.</summary>
public static class ConnectionStoreChecks
{
    public static int Verify()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cerebral-connection-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "connection.json");
        var passed = 0;
        void Check(bool condition) { if (!condition) throw new InvalidOperationException("Isolated connection-store verification failed."); passed++; }
        try
        {
            var missing = ConnectionStore.Read(path);
            Check(!missing.Saved && missing.Preferences?.Url == CerebralDefaults.WorkerUrl);
            Check(missing.Preferences?.AutoStartLocalServices == true);
            var first = ConnectionStore.SaveFromIntegrator(new() { Url = "http://127.0.0.1:16123", AccessKey = "fixture-only", AutoStartLocalServices = false }, missing.Revision, path);
            Check(first.Saved && first.Error is null && ConnectionStore.Read(path).Preferences?.AccessKey == "fixture-only");
            Check(ConnectionStore.Read(path).Preferences?.AutoStartLocalServices == false);
            var updated = ConnectionStore.SaveFromIntegrator(new() { Url = "http://127.0.0.1:16124" }, first.Revision, path);
            Check(updated.Revision != first.Revision && ConnectionStore.Read(path).Preferences?.Url.EndsWith(":16124", StringComparison.Ordinal) == true);
            var staleDenied = false;
            try { ConnectionStore.SaveFromIntegrator(new(), first.Revision, path); } catch (InvalidOperationException) { staleDenied = true; }
            Check(staleDenied && ConnectionStore.Read(path).Revision == updated.Revision);
            File.WriteAllText(path, "{");
            var broken = ConnectionStore.Read(path);
            Check(broken.Preferences is null && broken.Error?.Contains("Integrator", StringComparison.Ordinal) == true);
            var repaired = ConnectionStore.SaveFromIntegrator(new(), broken.Revision, path);
            Check(repaired.Preferences is not null && repaired.Error is null);
            var invalidDenied = false;
            try { ConnectionStore.SaveFromIntegrator(new() { Url = "http://192.0.2.1:6123", AccessKey = "" }, repaired.Revision, path); } catch (ArgumentException) { invalidDenied = true; }
            Check(invalidDenied && ConnectionStore.Read(path).Revision == repaired.Revision);
            File.WriteAllText(path, Ui.Serialize(new ConnectionPreferences { SchemaVersion = 2 }));
            Check(ConnectionStore.Read(path).Preferences is null);
            File.WriteAllText(path, "{\"schemaVersion\":1,\"url\":\"http://127.0.0.1:6123\",\"accessKey\":\"\"}");
            Check(ConnectionStore.Read(path).Preferences?.AutoStartLocalServices == true);
            Check(ConnectionStore.Read(path).Preferences?.Transport == "http");
            var legacy = ConnectionStore.Read(path);
            var endpoint = new LocalWorkerEndpoint("Pegasus.Cerebral.ConnectionCheck." + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            var pipe = ConnectionPreferences.ForLocalPipe(endpoint, Path.Combine(directory, "data"), autoStart: false);
            var savedPipe = ConnectionStore.SaveFromIntegrator(pipe, legacy.Revision, path);
            Check(savedPipe.Error is null && savedPipe.Preferences?.LocalEndpoint == endpoint && savedPipe.Preferences.Transport == "pipe");
            Check(savedPipe.Preferences?.Url == "" && savedPipe.Preferences.AccessKey == "" && !savedPipe.Preferences.AutoStartLocalServices);
            using (var client = savedPipe.Preferences!.CreateClient()) Check(client.IsLocalPipe);
            var unsupportedUpload = false;
            using (var client = pipe.CreateClient())
                try { client.UploadModelAsync(Path.Combine(directory, "does-not-exist.gguf"), "fixture").GetAwaiter().GetResult(); }
                catch (NotSupportedException) { unsupportedUpload = true; }
            Check(unsupportedUpload);
            var host = new HostInfo(LocalWorkerProtocol.Product, "fixture", LocalWorkerProtocol.Version, "fixture", "x64", "Windows", false, pipe.LocalDataDirectory!, true);
            pipe.ValidateHost(host); Check(true);
            var wrongDataDenied = false;
            try { pipe.ValidateHost(host with { DataDirectory = directory }); } catch (InvalidOperationException) { wrongDataDenied = true; }
            Check(wrongDataDenied);
            pipe.Url = "http://127.0.0.1:6123";
            var mixedDenied = false;
            try { ConnectionStore.SaveFromIntegrator(pipe, savedPipe.Revision, path); } catch (ArgumentException) { mixedDenied = true; }
            Check(mixedDenied && ConnectionStore.Read(path).Revision == savedPipe.Revision);
            pipe.Url = ""; pipe.LocalEndpoint = null;
            var absentEndpointDenied = false;
            try { ConnectionStore.SaveFromIntegrator(pipe, savedPipe.Revision, path); } catch (ArgumentException) { absentEndpointDenied = true; }
            Check(absentEndpointDenied);
            var installed = ConnectionPreferences.ForInstalledPipe(autoStart: true);
            var savedInstalled = ConnectionStore.SaveFromIntegrator(installed, savedPipe.Revision, path);
            Check(savedInstalled.Preferences is { Transport: "installed-pipe", AutoStartLocalServices: true,
                Url.Length: 0, AccessKey.Length: 0, LocalEndpoint: null, LocalDataDirectory: null });
            using (var installedClient = savedInstalled.Preferences!.CreateClient()) Check(installedClient.IsLocalPipe);
            installed.Url = "http://127.0.0.1:6123";
            var installedMixedDenied = false;
            try { ConnectionStore.SaveFromIntegrator(installed, savedInstalled.Revision, path); }
            catch (ArgumentException) { installedMixedDenied = true; }
            Check(installedMixedDenied && ConnectionStore.Read(path).Revision == savedInstalled.Revision);
            var installedUser = ConnectionPreferences.ForInstalledUserPipe(autoStart: false);
            var savedUser = ConnectionStore.SaveFromIntegrator(installedUser, savedInstalled.Revision, path);
            Check(savedUser.Preferences is { Transport: "installed-user-pipe", AutoStartLocalServices: false,
                Url.Length: 0, AccessKey.Length: 0, LocalEndpoint: null, LocalDataDirectory: null });
            installedUser.LocalDataDirectory = directory;
            var userMixedDenied = false;
            try { ConnectionStore.SaveFromIntegrator(installedUser, savedUser.Revision, path); }
            catch (ArgumentException) { userMixedDenied = true; }
            Check(userMixedDenied && ConnectionStore.Read(path).Revision == savedUser.Revision);
            File.WriteAllText(path, "{\"schemaVersion\":1,\"transport\":\"http\",\"transport\":\"pipe\"}");
            var duplicate = ConnectionStore.Read(path);
            Check(duplicate.Preferences is null && duplicate.Saved &&
                duplicate.Error?.Contains("Integrator", StringComparison.Ordinal) == true);
            File.WriteAllText(path, "{\"schemaVersion\":1,\"transport\":\"automatic\"}");
            Check(ConnectionStore.Read(path).Preferences is null);
            return passed + LocalWorkerDiscoveryChecks.Verify();
        }
        finally
        {
            var full = Path.GetFullPath(directory);
            if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("cerebral-connection-check-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the exact connection-check directory.");
            Directory.Delete(full, true);
        }
    }
}
