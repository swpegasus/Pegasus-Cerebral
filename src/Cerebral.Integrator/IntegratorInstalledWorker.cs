using System.IO;
using System.Security.Principal;
using System.Windows;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private sealed class OwnerPipePreview
    {
        public bool Ready { get; set; }
        public string Owner { get; set; } = "";
        public string OwnerSid { get; set; } = "";
        public string Service { get; set; } = "";
        public string Worker { get; set; } = "";
        public string Data { get; set; } = "";
        public bool PreservesBindingId { get; set; }
        public bool ChangesMachine { get; set; }
    }

    private async Task SetupInstalledWorker()
    {
        RequireWorkerManagementAllowed("enroll an installed Worker");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Installed Worker setup requires Windows.");
        if (!ManagementProcess.UsesRegisteredInstallation)
            throw new InvalidOperationException("Installed Worker setup requires the registered installation. A copied application can use its configured data without registering itself as the Windows service.");

        using var identity = WindowsIdentity.GetCurrent();
        var ownerSid = identity.User?.Value
            ?? throw new UnauthorizedAccessException("The signed-in Windows user could not be identified.");
        var existing = InstalledWorkerBindingStore.ReadRegistered();
        var defaultData = existing?.DataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Pegasus", "Cerebral");
        var form = new FieldsDialog(this, "Set up installed Worker",
            "Choose the installed data folder. The next screen resolves and displays the signed-in Windows owner and exact service paths before Windows asks for elevation.", "Preview")
            .Add("data", "Installed data folder", defaultData);
        if (form.ShowDialog() != true) return;
        var data = Path.GetFullPath(form.Value("data"));
        var previewText = await ManagementProcess.RunAsync(
            ["inspect-owner-pipe", "--owner-sid", ownerSid, "--data", data], Lifetime.Token);
        var preview = Ui.Parse<OwnerPipePreview>(previewText);
        if (!preview.Ready || preview.ChangesMachine || preview.OwnerSid != ownerSid ||
            !string.Equals(preview.Data, data, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installed Worker preview did not match the requested owner and data folder.");

        var action = preview.PreservesBindingId ? "update" : "register";
        if (!Confirm($"Review the installed Worker {action}:\n\n" +
                     $"Windows owner: {preview.Owner}\nOwner SID: {preview.OwnerSid}\n" +
                     $"Service: {preview.Service}\nWorker: {preview.Worker}\nData: {preview.Data}\n\n" +
                     "The Worker service must already be stopped. Continue to the Windows elevation prompt?")) return;

        SetStatus("Waiting for Windows to apply the reviewed installed Worker configuration…");
        await ManagementProcess.RunElevatedAsync(
            ["register-owner-pipe", "--owner-sid", ownerSid, "--confirmed-owner-sid", ownerSid, "--data", data], Lifetime.Token);
        var enrolled = InstalledWorkerBindingStore.ReadRegistered()
            ?? throw new IOException("Windows management returned without an installed Worker binding.");
        if (enrolled.OwnerSid != ownerSid || !InstalledWorkerBinding.PathEquals(enrolled.DataDirectory, data, true) ||
            !InstalledWorkerBinding.PathEquals(enrolled.WorkerExecutable, preview.Worker, false))
            throw new InvalidDataException("The protected installed Worker binding does not match the reviewed owner and paths.");

        var saved = ConnectionStore.Read();
        saved = ConnectionStore.SaveFromIntegrator(
            ConnectionPreferences.ForInstalledPipe(saved.Preferences?.AutoStartLocalServices ?? true), saved.Revision);
        SetStatus("Installed Worker registered. Starting and verifying its owner pipe…");
        await LocalServiceStartup.EnsureInstalledPipeAsync(saved, Lifetime.Token);
        await ConnectAsync();
        SetStatus("Installed Worker is connected through its protected owner pipe.");
    }
}
