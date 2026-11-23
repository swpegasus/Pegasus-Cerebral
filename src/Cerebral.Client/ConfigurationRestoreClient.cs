using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Client;

public sealed record ConfigurationRestoreClientSnapshot(ConfigurationRestoreApplyRequest Request,
    bool Dispatched, bool Uncertain, ConfigurationRestoreResult? Result, ConfigurationRestoreReceipt? Receipt);

public sealed class ConfigurationRestoreRejectedException(string message) : InvalidOperationException(message);

/// <summary>One reviewed apply on one captured transport; reconciliation only reads its retained receipt.</summary>
public sealed class ConfigurationRestoreClient
{
    private readonly ConfigurationRestoreApplyRequest request;
    private readonly Func<string, object, CancellationToken, Task<CommandResult>> command;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool dispatched, uncertain;
    private ConfigurationRestoreResult? result;
    private ConfigurationRestoreReceipt? receipt;

    public ConfigurationRestoreClient(ConfigurationRestoreApplyRequest request, CerebralClient client)
        : this(request, (operation, args, ct) => client.CommandAsync(operation, args, cancellationToken: ct)) { }

    public ConfigurationRestoreClient(ConfigurationRestoreApplyRequest request,
        Func<string, object, CancellationToken, Task<CommandResult>> command)
    {
        if (!Guid.TryParseExact(request.PreviewId, "N", out var id) || id == Guid.Empty ||
            request.ExpectedHostRevision < 0 || request.ExpectedHostRevision == int.MaxValue || !request.ReviewedFreeText)
            throw new ArgumentException("A reviewed restore preview with its exact revision is required.");
        this.request = request; this.command = command ?? throw new ArgumentNullException(nameof(command));
    }

    public ConfigurationRestoreClientSnapshot Snapshot => new(request, dispatched, uncertain, Copy(result), Copy(receipt));

    public async Task<ConfigurationRestoreResult> ApplyOnceAsync(CancellationToken ct = default)
    {
        if (!await gate.WaitAsync(0, ct).ConfigureAwait(false)) throw new InvalidOperationException("A restore request is already in progress.");
        try
        {
            if (result is not null) return Copy(result)!;
            if (dispatched) throw new InvalidOperationException("This restore was already dispatched. Check its retained status; do not apply it again.");
            ct.ThrowIfCancellationRequested();
            dispatched = true; uncertain = true;
            var reply = await command("configuration.restore.apply", request, ct).ConfigureAwait(false);
            if (!reply.Success)
            {
                uncertain = false;
                throw new ConfigurationRestoreRejectedException(reply.Error ?? "The service rejected this restore.");
            }
            var accepted = CerebralJson.Read<ConfigurationRestoreResult>(reply.Data);
            ValidateResult(accepted);
            result = Copy(accepted); uncertain = false;
            return Copy(result)!;
        }
        finally { gate.Release(); }
    }

    public async Task<ConfigurationRestoreStatus> RefreshAsync(CancellationToken ct = default)
    {
        if (!await gate.WaitAsync(0, ct).ConfigureAwait(false)) throw new InvalidOperationException("A restore request is already in progress.");
        try
        {
            var reply = await command("configuration.restore.status", new ConfigurationRestoreStatusRequest(request.PreviewId), ct).ConfigureAwait(false);
            if (!reply.Success) throw new ConfigurationRestoreRejectedException(reply.Error ?? "The service could not read this restore status.");
            var status = CerebralJson.Read<ConfigurationRestoreStatus>(reply.Data);
            if (status.PreviewId != request.PreviewId || status.MaximumRetainedReceipts is < 1 or > 10000 ||
                status.RetentionDays is < 1 or > 3650 ||
                status.State is not ("committed" or "unknown") || (status.State == "committed") != (status.Receipt is not null))
                throw new InvalidDataException("The service returned a mismatched restore status.");
            if (status.Receipt is { } saved)
            {
                if (saved.PreviewId != request.PreviewId || saved.ExpectedHostRevision != request.ExpectedHostRevision ||
                    saved.ReviewedFreeText != request.ReviewedFreeText || saved.AppliedAt == default ||
                    saved.NotificationOutcome is not ("pending" or "not-requested" or "delivered" or "failed"))
                    throw new InvalidDataException("The retained restore receipt does not match the reviewed request.");
                ValidateResult(saved.Result);
                if (result is not null && JsonSerializer.Serialize(result, CerebralJson.Options) != JsonSerializer.Serialize(saved.Result, CerebralJson.Options))
                    throw new InvalidDataException("The service changed an already confirmed restore result.");
                result = Copy(saved.Result); receipt = Copy(saved); uncertain = false;
            }
            // A missing or expired receipt cannot invalidate a prior confirmation or clear uncertainty.
            return Copy(status)!;
        }
        finally { gate.Release(); }
    }

    private void ValidateResult(ConfigurationRestoreResult value)
    {
        static bool Ids(IReadOnlyList<string>? items, int maximum) => items is not null && items.Count <= maximum &&
            items.All(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 128) && items.Distinct(StringComparer.Ordinal).Count() == items.Count;
        if (value is null || value.HostRevision != request.ExpectedHostRevision + 1 || value.Mode != "upsert" ||
            !Ids(value.AgentIds, 128) || !Ids(value.ModelIds, 128) ||
            value.PromptRoles is { } roles && (!Ids(roles, 4) || roles.Any(x => x is not ("system" or "lead" or "helper" or "compactor"))))
            throw new InvalidDataException("The service returned an invalid restore result.");
    }
    private static T? Copy<T>(T? value) where T : class =>
        value is null ? null : CerebralJson.Read<T>(CerebralJson.Element(value));
}
