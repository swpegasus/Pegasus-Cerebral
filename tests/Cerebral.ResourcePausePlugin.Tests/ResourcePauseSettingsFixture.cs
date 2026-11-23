using System.Text.Json;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.ResourcePausePlugin.Tests;

public static partial class Program
{
    // Narrow in-memory owner for the settings operations used by this fixture. It is not service-store integration evidence.
    private sealed partial class FixtureHost
    {
        private static readonly PluginSettingsCoordinate SettingsCoordinate = new("pegasus.resource-pause", SettingsPreferenceContract.GlobalScope);
        public PluginSettingsState SettingsState { get; private set; } = new(SettingsCoordinate, 0, null, [], "owner-client");
        public Func<string, object, CancellationToken, Task<DesktopServiceCommandResult>>? SettingsReply { get; set; }

        private static bool IsSettingsOperation(string operation) => operation is CompleteSettingsContract.DescribeOperation or
            CompleteSettingsContract.PluginGetOperation or CompleteSettingsContract.PluginSaveOperation;

        public DesktopServiceCommandResult DefaultSettingsReply(string operation, object arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (operation == CompleteSettingsContract.DescribeOperation)
                return SettingsResult(new CompleteSettingsCapabilities(CompleteSettingsContract.SchemaVersion,
                    ConfigurationBackupContract.SchemaVersion, CompleteSettingsContract.BackupSchemaVersion,
                    [SettingsTransferPurpose.Portable, SettingsTransferPurpose.Complete],
                    "configuration.presets.export", "configuration.backup.export",
                    CompleteSettingsContract.PluginGetOperation, CompleteSettingsContract.PluginSaveOperation));
            var raw = CerebralJson.Element(arguments);
            if (operation == CompleteSettingsContract.PluginGetOperation)
            {
                if (raw.Deserialize<PluginSettingsCoordinate>(CerebralJson.Options) != SettingsCoordinate)
                    return SettingsRejected("fixture settings coordinate mismatch");
                return SettingsResult(new PluginSettingsTransferState(SettingsState, []));
            }
            if (operation != CompleteSettingsContract.PluginSaveOperation)
                throw new InvalidOperationException("Unsupported fixture settings operation " + operation);
            var transfer = raw.Deserialize<PluginSettingsTransferSaveRequest>(CerebralJson.Options)
                ?? throw new InvalidOperationException("Missing fixture settings save");
            var save = transfer.Save;
            if (save.Document.Coordinate != SettingsCoordinate || save.Document.SourcePackageVersion != "1.1.5" ||
                save.Document.SettingsVersion != 1 || save.Document.Values.ValueKind != JsonValueKind.Object ||
                !save.OwnerValidated || save.PortableValues is not { ValueKind: JsonValueKind.Object } ||
                save.OmittedPortableFields < 0 || save.PendingId is not null || transfer.CompleteAdmitted)
                return SettingsRejected("fixture settings save unsupported");
            if (save.ExpectedRevision != SettingsState.Revision)
                return SettingsRejected("fixture settings revision conflict");
            var document = save.Document with { Values = save.Document.Values.Clone() };
            var changed = SettingsState.Active is null ||
                !JsonElement.DeepEquals(CerebralJson.Element(SettingsState.Active), CerebralJson.Element(document)) ||
                !JsonElement.DeepEquals(SettingsState.PortableValues ?? CerebralJson.Element(new { }), save.PortableValues.Value) ||
                SettingsState.OmittedPortableFields != save.OmittedPortableFields;
            if (changed)
                SettingsState = new(SettingsCoordinate, checked(SettingsState.Revision + 1), document, [],
                    "owner-client", save.PortableValues.Value.Clone(), save.OmittedPortableFields);
            return SettingsResult(new PluginSettingsTransferSaveResult(new(SettingsState, []), changed));
        }

        private static DesktopServiceCommandResult SettingsResult(object value) => new(true, CerebralJson.Element(value));
        private static DesktopServiceCommandResult SettingsRejected(string error) => new(false, default, error);
    }
}
