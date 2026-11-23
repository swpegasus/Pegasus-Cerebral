using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Cerebral.Core;
using Cerebral.Contracts.Workflows;
using Cerebral.Tools;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflowEffects
{
    private const string InternetReviewCommand = "cerebral.host:internet-review";
    private InternetPolicies InternetPolicyService()
    {
        var host = workflowHost?.Invoke() ?? throw new AdmissionException("The authenticated Main workflow host is unavailable.");
        return new(store, configuration, host, diagnosticEvents ??
            throw new AdmissionException("The native owner event route is unavailable."), ManagedInternetCatalog);
    }
    private RegisteredToolCatalog ManagedInternetCatalog() => tools is ToolDispatcher registered
        ? ChatRegisteredToolCatalog.Bind(registered.ListManagedInternet(), workflowSettingsRuntime)
        : throw new InternetAccessException("internet-provider-unavailable", "The managed web dispatcher is unavailable.");
    private WorkflowCommandResult? PrepareInternet(WorkflowCommandContext context, ToolArguments args,
        out IManagedInternetSession? session)
    {
        session = null;
        if (!InternetPolicies.Managed(args.Name)) return null;
        var host = workflowHost?.Invoke() ?? throw new AdmissionException("The owning Main host is unavailable.");
        var entry = ManagedInternetCatalog().Tools.SingleOrDefault(x => x.Descriptor.Name == args.Name) ??
            throw new InternetAccessException("internet-provider-unavailable", "The exact tool provider does not implement managed web version one.");
        var source = host.InternetAuthority(context.Submission,
            context.RunId ?? throw new AdmissionException("The source run is unavailable."),
            context.EffectId, args.Name, entry.SourceFingerprint);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context.Arguments.GetRawText()))).ToLowerInvariant();
        if (args.Arguments.TryGetProperty("url", out var suppliedUrl) &&
            (suppliedUrl.ValueKind != JsonValueKind.String || !Uri.TryCreate(suppliedUrl.GetString(), UriKind.Absolute, out var supplied) ||
                supplied.Scheme is not ("http" or "https") || supplied.UserInfo.Length != 0))
            throw new InternetAccessException("internet-url-invalid", "An HTTP(S) URL without embedded credentials is required; no approval or request was submitted.");
        string? origin = args.Arguments.TryGetProperty("url", out var raw) && raw.ValueKind == JsonValueKind.String &&
            Uri.TryCreate(raw.GetString(), UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
            ? new UriBuilder(url.Scheme, url.Host, url.Port).Uri.GetLeftPart(UriPartial.Authority) : null;
        var policies = InternetPolicyService();
        if (policies.Preflight(source.Authority, source.RootRevision, hash, origin) is { } action)
            return new(WorkflowEffectState.Failed, Element(new ToolData(args.Name, "", false, null) { InternetAction = action }),
                "Managed web access is waiting for its exact native owner review. No request was sent.");
        var authority = source.Authority;
        session = InternetRuntime.For(store, diagnosticEvents!, tools as ToolDispatcher ?? throw new AdmissionException("The managed dispatcher is unavailable.")).Open(authority, () =>
        {
            var currentSource = host.InternetAuthority(context.Submission, authority.RunId,
                authority.EffectId, authority.ToolName, authority.ToolFingerprint);
            if (currentSource.Authority != authority)
                throw new InternetAccessException("internet-owner-stale", "The accepted Main Internet identity changed.");
            var (snapshot, effective) = Resolve(context.Submission);
            var descriptor = snapshot.Tools.SingleOrDefault(x => x.Name == authority.ToolName);
            var current = ManagedInternetCatalog().Tools.SingleOrDefault(x => x.Descriptor.Name == authority.ToolName);
            if (descriptor is null || current is null || current.SourceFingerprint != authority.ToolFingerprint ||
                !WorkerExecutionSnapshot.SameTool(descriptor, current.Descriptor) ||
                !WorkerExecutionSnapshot.AllowsTool(effective, authority.ToolName) ||
                !effective.Capabilities.Contains(descriptor.Capability, StringComparer.Ordinal))
                throw new InternetAccessException("internet-tool-revoked", "The exact network tool/provider or profile grant changed.");
            return policies.RequireAllowed(authority);
        });
        return null;
    }
    private sealed record InternetReviewArguments(string RequestId, string RequestDigest,
        string OriginalEffectId, string ArgumentsSha256);
    private sealed class InternetReviewHandler(WorkerWorkflowEffects owner) : IWorkflowCommandHandler
    {
        public WorkflowCommandDescriptor Descriptor { get; } = new(InternetReviewCommand, 1,
            "Inspect an exact saved owner Internet decision without granting or sending network traffic.",
            Schema("""
            {"type":"object","properties":{
              "requestId":{"type":"string","minLength":64,"maxLength":64},"requestDigest":{"type":"string","minLength":64,"maxLength":64},
              "originalEffectId":{"type":"string","minLength":1,"maxLength":128},"argumentsSha256":{"type":"string","minLength":64,"maxLength":64}},
              "required":["requestId","requestDigest","originalEffectId","argumentsSha256"],"additionalProperties":false}
            """),
            Schema("""
            {"type":"object","properties":{
              "requestId":{"type":"string"},"requestDigest":{"type":"string"},"originalEffectId":{"type":"string"},
              "argumentsSha256":{"type":"string"},"allowed":{"type":"boolean"}},
              "required":["requestId","requestDigest","originalEffectId","argumentsSha256","allowed"],"additionalProperties":false}
            """), [], TimeoutSeconds: 15);
        private WorkflowCommandResult Inspect(WorkflowCommandContext context)
        {
            var args = InternetJson.Read<InternetReviewArguments>(context.Arguments);
            var runId = context.RunId ?? throw new AdmissionException("The native Internet review source is unavailable.");
            var allowed = owner.InternetPolicyService().Review(args.RequestId, args.RequestDigest,
                runId, args.OriginalEffectId, args.ArgumentsSha256);
            return new(WorkflowEffectState.Succeeded, Element(new { args.RequestId, args.RequestDigest,
                args.OriginalEffectId, args.ArgumentsSha256, allowed }));
        }
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(Inspect(context)); }
        // This is a read-only inspection. Reconciliation reads the same durable decision; it never grants or resends.
        public Task<WorkflowCommandResult> ReconcileAsync(WorkflowCommandContext context, CancellationToken token) => ExecuteAsync(context, token);
    }
}
