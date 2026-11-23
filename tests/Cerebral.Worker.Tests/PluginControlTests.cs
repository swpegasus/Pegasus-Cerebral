using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Workflows;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

/// <summary>SQLite/metadata fixtures only: no executable plugin is loaded and no native control is clicked.</summary>
public static partial class PluginControlTests
{
    public static async Task RunAsync(bool verbose = true)
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            checks++;
            if (verbose) Console.WriteLine("PASS: " + name);
        }
        bool Rejects(Action action)
        {
            try { action(); return false; }
            catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException or IOException or InvalidOperationException or InvalidDataException)
            { return true; }
        }
        static T Copy<T>(T value) => CerebralJson.Read<T>(CerebralJson.Element(value));

        using var f = new Fixture();
        var context = f.Context();
        var presentation = f.Present(context);
        var request = f.Controls.Create(context, presentation.Id, "inspect", f.MountId, null);
        Check(request.Id == context.EffectId && request.Plugin.Generation == f.Plugin.Generation &&
              request.Plugin.ManifestPath == f.Plugin.ManifestPath && request.Status == "pending" &&
              f.Store.GetSessionMetadata(context.SessionId) is null,
            "native control admission binds the durable host effect and exact owner-installed generation without a legacy TaskSession");
        Check(request.PackageFingerprint == NativeDesktopPluginPackage.ComputeFingerprint(f.Plugin.ManifestPath),
            "new native control requests capture the fingerprint of the approved installed package");
        var reopened = new PluginControlRequests(new StateStore(f.Store.DirectoryPath), f.Events);
        Check(reopened.Read(context, request.Id).Status == "pending",
            "pending control state survives reopening the actual SQLite store");
        var subscription = f.Events.Subscribe();
        Check(f.Controls.Create(context, presentation.Id, "inspect", f.MountId, null).Id == request.Id &&
              !subscription.Reader.TryRead(out _),
            "identical host-effect retries return the existing record without publishing a second dispatch");
        f.Events.Unsubscribe(subscription.Id);
        Check(Rejects(() => f.Controls.Create(context, presentation.Id, "invoke", f.MountId, "Increment")),
            "a different operation under the same host effect is rejected");
        Check(Rejects(() => f.Controls.Create(context with { EffectId = null }, presentation.Id, "inspect", f.MountId, null)) &&
              Rejects(() => f.Controls.Create(context with { EffectId = Guid.Empty.ToString("N") }, presentation.Id, "inspect", f.MountId, null)),
            "missing and empty model-independent host effect identities are rejected");
        Check(Rejects(() => f.Controls.Create(f.Context(), presentation.Id, "inspect", "other.panel", null)),
            "only a mount recorded in the installed receipt is admitted");
        Check(Rejects(() => f.Controls.Create(context with { SessionId = "another-conversation", EffectId = Fixture.Id() },
                  presentation.Id, "inspect", f.MountId, null)) &&
              Rejects(() => f.Controls.Read(context with { SessionId = "another-conversation" }, request.Id)),
            "retained conversation ownership is enforced for creation and reads");
        var other = Copy(f.Profile); other.Id = "other-lead"; f.Store.Put("agent", other.Id, other);
        Check(Rejects(() => f.Controls.Create(context with { Profile = other, EffectId = Fixture.Id() }, presentation.Id, "inspect", f.MountId, null)) &&
              Rejects(() => f.Controls.Read(context with { Profile = other }, request.Id)),
            "another local lead cannot use a retained presentation or control receipt");
        Check(Rejects(() => f.Controls.Create(f.Context(), Fixture.Id(), "inspect", f.MountId, null)),
            "unknown presentation IDs disclose no foreign receipt");
        var helper = Copy(f.Profile); helper.Role = "compactor";
        var remote = Copy(f.Profile); remote.WorkerId = "remote";
        Check(Rejects(() => f.Controls.Create(context with { Profile = helper, EffectId = Fixture.Id() }, presentation.Id, "inspect", f.MountId, null)) &&
              Rejects(() => f.Controls.Create(context with { Profile = remote, EffectId = Fixture.Id() }, presentation.Id, "inspect", f.MountId, null)),
            "a compactor or remote accepted profile cannot borrow the local owner control path");
        var current = Copy(f.Profile); current.Capabilities.Remove("desktop.plugins.manage"); f.Store.Put("agent", current.Id, current);
        Check(Rejects(() => f.Controls.Create(f.Context(), presentation.Id, "inspect", f.MountId, null)) &&
              Rejects(() => f.Controls.Read(context, request.Id)),
            "revoked current capability defeats stale accepted profile grants for creation and reads");
        f.RestoreProfile();
        current = Copy(f.Profile); current.AllowedTools.Remove(PluginControlToolExtension.ToolName); f.Store.Put("agent", current.Id, current);
        Check(Rejects(() => f.Controls.Create(f.Context(), presentation.Id, "inspect", f.MountId, null)),
            "revoked exact current tool grant blocks control admission");
        f.RestoreProfile();
        current = Copy(f.Profile); current.Role = "compactor"; f.Store.Put("agent", current.Id, current);
        Check(Rejects(() => f.Controls.Read(context, request.Id)), "current local-author role is rechecked at receipt reads");
        f.RestoreProfile();
        current = Copy(f.Profile); current.WorkerId = "remote"; f.Store.Put("agent", current.Id, current);
        Check(Rejects(() => f.Controls.Read(context, request.Id)), "current worker locality is rechecked at receipt reads");
        f.RestoreProfile();
        current = Copy(f.Profile); current.AllowedTools = ["*"]; f.Store.Put("agent", current.Id, current);
        var wildcard = f.Controls.Create(f.Context(), presentation.Id, "inspect", f.MountId, null);
        Check(wildcard.Status == "pending", "an explicit current wildcard grant admits the fixed control tool");
        f.Controls.Interrupt(context, wildcard.Id, true); f.RestoreProfile();

        var unapprovedContext = f.Context();
        var unapproved = f.Present(unapprovedContext, testsAllowed: false);
        Check(Rejects(() => f.Controls.Create(unapprovedContext, unapproved.Id, "inspect", f.MountId, null)),
            "owner installation alone does not opt a plugin into control testing");
        var pendingContext = f.Context();
        var pendingPresentation = f.Present(pendingContext, complete: false);
        Check(Rejects(() => f.Controls.Create(pendingContext, pendingPresentation.Id, "inspect", f.MountId, null)),
            "pending presentation cannot be used as installed-plugin evidence");
        var missingContext = f.Context();
        var missingBinding = f.Present(missingContext, includePlugin: false);
        Check(Rejects(() => f.Controls.Create(missingContext, missingBinding.Id, "inspect", f.MountId, null)),
            "legacy completed presentation without typed immutable plugin binding is rejected");
        var driftContext = f.Context();
        var driftPresentation = f.Present(driftContext);
        var drift = f.Controls.Create(driftContext, driftPresentation.Id, "inspect", f.MountId, null);
        driftPresentation.Plugin = driftPresentation.Plugin! with { Generation = "1.0.0-" + Fixture.Id() };
        f.Store.Put("interface", driftPresentation.Id, driftPresentation);
        Check(!f.Controls.Claim(drift.Id, drift.SessionId, Fixture.Id()).Claimed &&
              f.Controls.Read(driftContext, drift.Id).Status == "rejected",
            "a retained installed generation changed after admission cannot be claimed");
        var revokeContext = f.Context();
        var revokePresentation = f.Present(revokeContext);
        var revoke = f.Controls.Create(revokeContext, revokePresentation.Id, "inspect", f.MountId, null);
        revokePresentation.Plugin = revokePresentation.Plugin! with { ControlTestsAllowed = false };
        f.Store.Put("interface", revokePresentation.Id, revokePresentation);
        Check(!f.Controls.Claim(revoke.Id, revoke.SessionId, Fixture.Id()).Claimed,
            "owner testing opt-in revoked after admission prevents delivery");
        var grantContext = f.Context();
        var grantPresentation = f.Present(grantContext);
        var grant = f.Controls.Create(grantContext, grantPresentation.Id, "inspect", f.MountId, null);
        current = Copy(f.Profile); current.AllowedTools.Clear(); f.Store.Put("agent", current.Id, current);
        Check(!f.Controls.Claim(grant.Id, grant.SessionId, Fixture.Id()).Claimed,
            "current grant revocation between admission and owner claim prevents delivery");
        f.RestoreProfile();

        Check(Rejects(() => f.Controls.Claim(request.Id, "foreign-conversation", Fixture.Id())) &&
              Rejects(() => f.Controls.Claim(request.Id, context.SessionId, "not-a-claim")),
            "owner claims need the retained conversation and a closed claim identity");
        var claims = await Task.WhenAll(Task.Run(() => f.Controls.Claim(request.Id, context.SessionId, Fixture.Id())),
            Task.Run(() => f.Controls.Claim(request.Id, context.SessionId, Fixture.Id())));
        var winner = claims.Single(x => x.Claimed).Request;
        Check(claims.Count(x => x.Claimed) == 1 && !reopened.Claim(request.Id, context.SessionId, Fixture.Id()).Claimed,
            "two competing owners claim once and reopened claimed effects do not replay");
        var inspected = f.Observation(click: false);
        Check(Rejects(() => f.Controls.Complete(request.Id, context.SessionId, Fixture.Id(), "completed", inspected, null)) &&
              Rejects(() => f.Controls.Complete(request.Id, "foreign-conversation", winner.ClaimId!, "completed", inspected, null)),
            "completion requires both the exact claim and selected owner conversation");
        var done = f.Controls.Complete(request.Id, context.SessionId, winner.ClaimId!, "completed", inspected, null);
        subscription = f.Events.Subscribe();
        var identical = f.Controls.Complete(request.Id, context.SessionId, winner.ClaimId!, "completed", Copy(inspected), null);
        Check(done.Status == "completed" && identical.Observation!.Before.Texts.SequenceEqual(inspected.Before.Texts) &&
              !subscription.Reader.TryRead(out _),
            "structurally identical observation retries survive serialization and publish no second outcome");
        f.Events.Unsubscribe(subscription.Id);
        var different = inspected with { After = inspected.After! with { Texts = ["different actual view"] } };
        Check(Rejects(() => f.Controls.Complete(request.Id, context.SessionId, winner.ClaimId!, "completed", different, null)) &&
              Rejects(() => f.Controls.Complete(request.Id, context.SessionId, winner.ClaimId!, "rejected", null, "different outcome")),
            "a terminal completion cannot silently replace its actual observation or outcome");
        Check(f.Controls.Create(context, presentation.Id, "inspect", f.MountId, null).Status == "completed",
            "a repeated terminal host effect returns its completion without a new click");

        var expire = f.NewControl("inspect");
        f.Expire(expire.Request);
        Check(!f.Controls.Claim(expire.Request.Id, expire.Context.SessionId, Fixture.Id()).Claimed &&
              f.Controls.Read(expire.Context, expire.Request.Id).Status == "rejected",
            "expiry before owner claim prevents any delivery");
        var cancel = f.NewControl("invoke");
        var cancelled = f.Controls.Interrupt(cancel.Context, cancel.Request.Id, true);
        Check(cancelled.Status == "cancelled" && !f.Controls.Claim(cancel.Request.Id, cancel.Context.SessionId, Fixture.Id()).Claimed &&
              f.Controls.Create(cancel.Context, cancel.Presentation.Id, "invoke", f.MountId, "Increment").Status == "cancelled",
            "cancellation before claim is durable and the original effect cannot replay");
        var quiet = f.NewControl("inspect");
        f.Expire(quiet.Request);
        Check((await f.Controls.WaitAsync(quiet.Context, quiet.Request.Id, CancellationToken.None)).Status == "rejected",
            "durable wait retires an offline unclaimed request at its deadline");
        var waitCancel = f.NewControl("inspect");
        using (var cancelledToken = new CancellationTokenSource())
        {
            cancelledToken.Cancel();
            Check((await f.Controls.WaitAsync(waitCancel.Context, waitCancel.Request.Id, cancelledToken.Token)).Status == "cancelled",
                "wait cancellation before owner claim does not require a desktop response");
        }
        var uncertain = f.NewControl("invoke");
        var uncertainClaim = f.Controls.Claim(uncertain.Request.Id, uncertain.Context.SessionId, Fixture.Id()).Request;
        Check(f.Controls.Interrupt(uncertain.Context, uncertain.Request.Id, true).Status == "uncertain" &&
              !reopened.Claim(uncertain.Request.Id, uncertain.Context.SessionId, Fixture.Id()).Claimed,
            "interruption after claim stays unconfirmed and never requeues an invocation");
        var late = f.Controls.Complete(uncertain.Request.Id, uncertain.Context.SessionId, uncertainClaim.ClaimId!,
            "completed", f.Observation(click: true), null);
        Check(late.Status == "completed" && late.Observation!.ClickDelivered &&
              !f.Controls.Claim(late.Id, late.SessionId, Fixture.Id()).Claimed,
            "late exact owner observation can confirm an interrupted effect without another delivery");

        var bounded = f.NewControl("invoke");
        var boundedClaim = f.Controls.Claim(bounded.Request.Id, bounded.Context.SessionId, Fixture.Id()).Request;
        bool BadObservation(PluginControlObservation value) => Rejects(() => f.Controls.Complete(bounded.Request.Id,
            bounded.Context.SessionId, boundedClaim.ClaimId!, "completed", value, null));
        var actual = f.Observation(click: true);
        Check(BadObservation(new PluginControlObservation(null!, actual.After, true)),
            "malformed observations require a real before-view instead of causing a null dereference");
        Check(BadObservation(actual with { Before = actual.Before with { Texts = Enumerable.Repeat("x", 21).ToArray() } }) &&
              BadObservation(actual with { Before = actual.Before with { Texts = [new string('x', 2049)] } }) &&
              BadObservation(actual with { Before = actual.Before with { Texts = [new string('x', 2048), new string('x', 2048), "x"] } }) &&
              BadObservation(actual with { Before = actual.Before with { Texts = ["bad\0text"] } }),
            "text observations reject too many lines, oversized lines, excessive total text, and NUL");
        Check(BadObservation(actual with { Before = actual.Before with { Buttons = Enumerable.Repeat(new PluginControlButton("x", true, true), 21).ToArray() } }) &&
              BadObservation(actual with { Before = actual.Before with { Buttons = [new PluginControlButton(new string('x', 257), true, true)] } }) &&
              BadObservation(actual with { Before = actual.Before with { Buttons = [new PluginControlButton("bad\tname", true, true)] } }),
            "button observations reject oversized counts, names, and control characters");
        Check(BadObservation(actual with { Before = actual.Before with { Id = "another.mount" } }) &&
              BadObservation(actual with { After = actual.After! with { Id = "another.mount" } }),
            "before and after observations must both describe the bound mount");
        Check(BadObservation(actual with { After = null }) && BadObservation(actual with { ClickDelivered = false }) &&
              BadObservation(actual with { InstanceRecreated = true }),
            "completed invocation requires the actual after-view and click evidence without claiming instance recreation");
        Check(Rejects(() => f.Controls.Complete(bounded.Request.Id, bounded.Context.SessionId, boundedClaim.ClaimId!,
                  "completed", actual, "also failed")) &&
              Rejects(() => f.Controls.Complete(bounded.Request.Id, bounded.Context.SessionId, boundedClaim.ClaimId!,
                  "uncertain", actual, null)) &&
              Rejects(() => f.Controls.Complete(bounded.Request.Id, bounded.Context.SessionId, boundedClaim.ClaimId!,
                  "failed", actual, "wrong status")) &&
              Rejects(() => f.Controls.Complete(bounded.Request.Id, bounded.Context.SessionId, boundedClaim.ClaimId!,
                  "rejected", null, new string('x', 2001))),
            "outcomes enforce the closed status set and bounded consistent errors");
        var inspector = f.NewControl("inspect");
        var inspectorClaim = f.Controls.Claim(inspector.Request.Id, inspector.Context.SessionId, Fixture.Id()).Request;
        Check(Rejects(() => f.Controls.Complete(inspector.Request.Id, inspector.Context.SessionId, inspectorClaim.ClaimId!,
                   "completed", f.Observation(click: true), null)),
            "an inspection cannot claim that it delivered a click");
        Check(Rejects(() => f.Controls.Complete(inspector.Request.Id, inspector.Context.SessionId, inspectorClaim.ClaimId!,
                   "completed", f.Observation(click: false, recreated: true), null)),
            "an inspection cannot claim that it recreated the plugin instance");
        var reopen = f.NewControl("reopen");
        var reopenClaim = f.Controls.Claim(reopen.Request.Id, reopen.Context.SessionId, Fixture.Id()).Request;
        Check(Rejects(() => f.Controls.Complete(reopen.Request.Id, reopen.Context.SessionId, reopenClaim.ClaimId!,
                   "completed", f.Observation(click: false), null)),
            "a completed reopen requires explicit new-instance evidence");
        var reopenedObservation = f.Observation(click: false, recreated: true);
        Check(f.Controls.Complete(reopen.Request.Id, reopen.Context.SessionId, reopenClaim.ClaimId!,
                  "completed", reopenedObservation, null).Observation?.InstanceRecreated == true,
            "a reopen retains truthful new-instance evidence without claiming a click");
        var legacyObservation = JsonSerializer.Deserialize<PluginControlObservation>(JsonSerializer.Serialize(new
        {
            before = reopenedObservation.Before, after = reopenedObservation.After, clickDelivered = false
        }, CerebralJson.Options), CerebralJson.Options)!;
        Check(!legacyObservation.InstanceRecreated,
            "older retained observations without lifecycle evidence deserialize as not recreated");
        f.Controls.Complete(bounded.Request.Id, bounded.Context.SessionId, boundedClaim.ClaimId!, "completed", actual, null);
        f.Controls.Complete(inspector.Request.Id, inspector.Context.SessionId, inspectorClaim.ClaimId!, "completed", inspected, null);

        var descriptor = f.Tool.Tools.Single();
        var operationSchemas = descriptor.InputSchema.GetProperty("oneOf").EnumerateArray().ToArray();
        JsonElement OperationSchema(string operation) => operationSchemas.Single(schema =>
            schema.GetProperty("properties").GetProperty("operation").GetProperty("enum")[0].GetString() == operation);
        var inspectSchema = OperationSchema("inspect");
        var setTextSchema = OperationSchema("setText");
        var invokeSchema = OperationSchema("invoke");
        var reopenSchema = OperationSchema("reopen");
        var controlProperties = reopenSchema.GetProperty("properties");
        var presentationReferences = controlProperties.GetProperty("presentationId").GetProperty("oneOf")
            .EnumerateArray().ToArray();
        Check(descriptor.PrerequisiteTools.SequenceEqual([InterfaceToolExtension.StatusToolName]),
            "native control preparation carries its optional exact presentation-status schema");
        Check(descriptor.Name == PluginControlToolExtension.ToolName && descriptor.Capability == "desktop.plugins.manage" &&
              operationSchemas.All(schema => !schema.GetProperty("additionalProperties").GetBoolean()) &&
              operationSchemas[0].GetProperty("required").EnumerateArray().Select(x => x.GetString())
                  .SequenceEqual(new[] { "presentationId", "operation", "mountId" }) &&
              descriptor.Description.Contains("plugin.present's requestId", StringComparison.Ordinal) &&
              descriptor.Description.Contains("literal reviewed", StringComparison.Ordinal) &&
              descriptor.Description.Contains("generation is never a presentation ID", StringComparison.Ordinal) &&
              descriptor.Description.Contains("receipt identifies only this control effect", StringComparison.Ordinal) &&
              descriptor.Description.Contains("genuinely new instance", StringComparison.Ordinal) &&
              operationSchemas.Select(schema => schema.GetProperty("properties").GetProperty("operation").GetProperty("enum")[0].GetString())
                  .SequenceEqual(new[] { "inspect", "setText", "invoke", "reopen" }) &&
              controlProperties.GetProperty("presentationId").GetProperty("description").GetString()!
                  .Contains("plugin.present", StringComparison.Ordinal) &&
              presentationReferences.Length == 2 &&
              presentationReferences[0].GetProperty("minLength").GetInt32() == 32 &&
              presentationReferences[0].GetProperty("maxLength").GetInt32() == 32 &&
              presentationReferences[1].GetProperty("enum").EnumerateArray().Single().GetString() == InterfaceRequests.ReviewedReference &&
              controlProperties.GetProperty("mountId").GetProperty("description").GetString()!
                  .Contains("interface.status", StringComparison.Ordinal) &&
              controlProperties.GetProperty("expectedTexts").GetProperty("minItems").GetInt32() == 1 &&
              controlProperties.GetProperty("expectedTexts").GetProperty("maxItems").GetInt32() == 8 &&
              controlProperties.GetProperty("expectedTexts").GetProperty("items").GetProperty("maxLength").GetInt32() == 256 &&
              descriptor.Description.Contains("expectedTexts", StringComparison.Ordinal),
            "the model catalog exposes one narrow control schema with explicit presentation, mount and receipt roles");
        Check(inspectSchema.GetProperty("properties").EnumerateObject().Select(field => field.Name)
                   .SequenceEqual(new[] { "presentationId", "operation", "mountId" }) &&
              setTextSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString())
                  .SequenceEqual(new[] { "presentationId", "operation", "mountId", "fieldId", "expectedText", "text" }) &&
              setTextSchema.GetProperty("properties").GetProperty("text").GetProperty("maxLength").GetInt32() == PluginControlTextEdits.MaximumCharacters &&
              invokeSchema.GetProperty("properties").GetProperty("expectedTexts").GetProperty("maxItems").GetInt32() == 8 &&
              invokeSchema.GetProperty("required").EnumerateArray().Any(value => value.GetString() == "buttonName") &&
              !reopenSchema.GetProperty("properties").TryGetProperty("buttonName", out _),
            "native control schemas expose only the fields accepted by their exact operation");
        WorkflowSchema.Validate(CerebralJson.Element(new
            { presentationId = presentation.Id, operation = "invoke", mountId = f.MountId, buttonName = "Increment" }), descriptor.InputSchema);
        WorkflowSchema.Validate(CerebralJson.Element(new
            { presentationId = presentation.Id, operation = "invoke", mountId = f.MountId, buttonName = "Increment", expectedTexts = new[] { "Count: 1" } }), descriptor.InputSchema);
        foreach (var invalidCall in new object[]
        {
            new { presentationId = presentation.Id, operation = "invoke", mountId = f.MountId },
            new { presentationId = presentation.Id, operation = "inspect", mountId = f.MountId, expectedTexts = new[] { "Count: 1" } },
            new { presentationId = presentation.Id, operation = "reopen", mountId = f.MountId, buttonName = "Increment" },
            new { presentationId = presentation.Id, operation = "setText", mountId = f.MountId, fieldId = Fixture.Id(), expectedText = "old" },
            new { presentationId = presentation.Id, operation = "setText", mountId = f.MountId, fieldId = Fixture.Id(), expectedText = "old", text = "new", buttonName = "Save" }
        })
            Check(Rejects(() => WorkflowSchema.Validate(CerebralJson.Element(invalidCall), descriptor.InputSchema)),
                "actual native control schema rejects operation-inapplicable fields or missing invoke target");
        WorkflowSchema.Validate(CerebralJson.Element(new
            { presentationId = InterfaceRequests.ReviewedReference, operation = "inspect", mountId = f.MountId }),
            descriptor.InputSchema);
        WorkflowSchema.Validate(CerebralJson.Element(new
            { presentationId = presentation.Id, operation = "inspect", mountId = f.MountId }),
            descriptor.InputSchema);
        WorkflowSchema.Validate(CerebralJson.Element(new
            { presentationId = presentation.Id, operation = "reopen", mountId = f.MountId, expectedTexts = new[] { "Count: 1" } }),
            descriptor.InputSchema);
        WorkflowSchema.Validate(CerebralJson.Element(new
            { presentationId = presentation.Id, operation = "setText", mountId = f.MountId, fieldId = Fixture.Id(), expectedText = "", text = "Line one\n世界" }),
            descriptor.InputSchema);
        Check(Rejects(() => WorkflowSchema.Validate(CerebralJson.Element(new
                { presentationId = "latest", operation = "inspect", mountId = f.MountId }), descriptor.InputSchema)),
            "the actual workflow schema accepts reviewed and concrete IDs but rejects arbitrary control presentation aliases");
        using (var reviewed = new Fixture())
        {
            var item = reviewed.Reviewed();
            var execution = reviewed.Tool.ExecuteAsync(reviewed.Call(InterfaceRequests.ReviewedReference),
                item.Context, CancellationToken.None);
            var retained = reviewed.Store.Get<PluginControlRequest>("plugin-control", item.Context.EffectId!)!;
            var claim = reviewed.Controls.Claim(retained.Id, retained.SessionId, Fixture.Id()).Request;
            reviewed.Controls.Complete(retained.Id, retained.SessionId, claim.ClaimId!, "completed",
                reviewed.Observation(click: false), null);
            var completed = await execution;
            Check(completed.Success && retained.PresentationId == item.Presentation.Id &&
                  retained.PresentationId != InterfaceRequests.ReviewedReference &&
                  JsonDocument.Parse(completed.Content).RootElement.GetProperty("presentationId").GetString() == item.Presentation.Id,
                "reviewed control admission persists and reports only the concrete bound presentation ID");
            var reconciledReviewed = await reviewed.Tool.ReconcileAsync(
                reviewed.Call(InterfaceRequests.ReviewedReference), item.Context, completed.Evidence!, CancellationToken.None);
            Check(reconciledReviewed.Success && reconciledReviewed.Evidence?.RequestId == retained.Id &&
                  reviewed.Store.List<PluginControlRequest>("plugin-control").Count == 1,
                "reviewed reconciliation resolves the same binding and reads one durable effect without replay");

            async Task<ToolResult> RejectedReviewed(ToolExecutionContext context) => await reviewed.Tool.ExecuteAsync(
                reviewed.Call(InterfaceRequests.ReviewedReference), context, CancellationToken.None);
            Check(!(await RejectedReviewed(item.Context with
                  { JobId = InterfaceRequests.PluginTestPrefix + Fixture.Id(), EffectId = Fixture.Id() })).Success &&
                  !(await RejectedReviewed(item.Context with
                  { JobId = "ordinary-run", EffectId = Fixture.Id() })).Success &&
                  !(await RejectedReviewed(item.Context with
                  { SessionId = "foreign-conversation", EffectId = Fixture.Id() })).Success,
                "reviewed controls reject wrong jobs and foreign conversations without creating an effect");
            var foreign = Copy(reviewed.Profile); foreign.Id = "foreign-reviewed-lead";
            reviewed.Store.Put("agent", foreign.Id, foreign);
            Check(!(await RejectedReviewed(item.Context with { Profile = foreign, EffectId = Fixture.Id() })).Success,
                "reviewed controls reject a foreign profile");

            var missing = reviewed.Reviewed();
            missing.Presentation.PluginTests = null;
            reviewed.Store.Put("interface", missing.Presentation.Id, missing.Presentation);
            Check(!(await RejectedReviewed(missing.Context)).Success,
                "reviewed controls require a retained continuation marker");
            var aliasUnapprovedContext = reviewed.Context();
            var aliasUnapproved = reviewed.Present(aliasUnapprovedContext, testsAllowed: false);
            Check(!(await RejectedReviewed(aliasUnapprovedContext with
                  { JobId = InterfaceRequests.PluginTestPrefix + aliasUnapproved.Id, EffectId = Fixture.Id() })).Success,
                "reviewed controls cannot manufacture owner approval");

            var revoked = reviewed.Reviewed();
            var revokedProfile = Copy(reviewed.Profile);
            revokedProfile.AllowedTools.Remove(PluginControlToolExtension.ToolName);
            reviewed.Store.Put("agent", revokedProfile.Id, revokedProfile);
            Check(!(await RejectedReviewed(revoked.Context)).Success,
                "reviewed controls recheck current plugin.controls grants before persisting an effect");
        }
        var malformed = new[]
        {
            CerebralJson.Element(new { presentationId = presentation.Id, operation = "inspect", mountId = f.MountId, approved = true }),
            CerebralJson.Element(new { operation = "inspect", mountId = f.MountId }),
            CerebralJson.Element(new { presentationId = 42, operation = "inspect", mountId = f.MountId }),
            CerebralJson.Element(new { presentationId = presentation.Id, operation = "install", mountId = f.MountId }),
            CerebralJson.Element(new { presentationId = Guid.Empty.ToString("N"), operation = "inspect", mountId = f.MountId }),
            CerebralJson.Element(new { presentationId = new string('z', 32), operation = "inspect", mountId = f.MountId }),
            CerebralJson.Element(new { presentationId = presentation.Id, operation = "inspect", mountId = f.MountId, buttonName = "Increment" }),
            CerebralJson.Element(new { presentationId = presentation.Id, operation = "invoke", mountId = f.MountId }),
            CerebralJson.Element(new { presentationId = presentation.Id, operation = "invoke", mountId = f.MountId, buttonName = new string('x', 257) }),
            CerebralJson.Element(new { presentationId = presentation.Id, operation = "reopen", mountId = f.MountId, buttonName = "Increment" }),
            CerebralJson.Element(new { presentationId = presentation.Id, operation = "setText", mountId = f.MountId, fieldId = "field-0", expectedText = "", text = "new" }),
            CerebralJson.Element(new { presentationId = presentation.Id, operation = "setText", mountId = f.MountId, fieldId = Fixture.Id(), expectedText = "same", text = "same" }),
            CerebralJson.Element(new { presentationId = presentation.Id, operation = "inspect", mountId = f.MountId, fieldId = Fixture.Id(), expectedText = "", text = "new" }),
            CerebralJson.Element(new { presentationId = presentation.Id, operation = "inspect", mountId = "bad\nmount" }),
            CerebralJson.Element(new { presentationId = presentation.Id, operation = "inspect", mountId = new string('x', 130) }),
            CerebralJson.Element(new[] { "not", "an", "object" })
        };
        var countBefore = f.Store.List<PluginControlRequest>("plugin-control").Count;
        var generationId = f.Plugin.Generation[^32..];
        var mistakenGeneration = await f.Tool.ExecuteAsync(f.Call(generationId), f.Context(), CancellationToken.None);
        Check(!mistakenGeneration.Success && mistakenGeneration.EffectiveOutcome == ToolExecutionOutcome.Rejected &&
              mistakenGeneration.Evidence is { ProviderId: PluginControlToolExtension.Provider, RequestId: null } &&
              mistakenGeneration.Content.Contains("plugin.present's exact requestId", StringComparison.Ordinal) &&
              mistakenGeneration.Content.Contains("generation", StringComparison.Ordinal) &&
              mistakenGeneration.Content.Contains("plugin.controls receipt", StringComparison.Ordinal) &&
              !mistakenGeneration.Content.Contains(presentation.Id, StringComparison.Ordinal) &&
              !mistakenGeneration.Content.Contains(f.MountId, StringComparison.Ordinal) &&
              f.Store.List<PluginControlRequest>("plugin-control").Count == countBefore,
            "a generation mistaken for presentationId gets actionable guidance without leaking admitted coordinates or persisting an effect");
        foreach (var arguments in malformed)
        {
            var result = await f.Tool.ExecuteAsync(new ToolCall(PluginControlToolExtension.ToolName, arguments),
                f.Context(), CancellationToken.None);
            Check(!result.Success && result.EffectiveOutcome == ToolExecutionOutcome.Rejected,
                "malformed or expanded control input is rejected: " + arguments.GetRawText()[..Math.Min(arguments.GetRawText().Length, 90)]);
        }
        var wrongTool = await f.Tool.ExecuteAsync(new ToolCall("owner.approve", malformed[0]), f.Context(), CancellationToken.None);
        Check(wrongTool.EffectiveOutcome == ToolExecutionOutcome.Rejected &&
              f.Store.List<PluginControlRequest>("plugin-control").Count == countBefore,
            "invalid tool names and schemas persist no control effects");
        using (var cancelledToken = new CancellationTokenSource())
        {
            cancelledToken.Cancel();
            var preCancelledContext = f.Context();
            try
            {
                await f.Tool.ExecuteAsync(f.Call(presentation.Id), preCancelledContext, cancelledToken.Token);
                Check(false, "pre-cancelled tool admission");
            }
            catch (OperationCanceledException)
            {
                Check(f.Store.Get<PluginControlRequest>("plugin-control", preCancelledContext.EffectId!) is null,
                    "pre-cancelled execution creates no control request");
            }
        }

        var executedContext = f.Context();
        var executing = f.Tool.ExecuteAsync(f.Call(presentation.Id, "invoke"), executedContext, CancellationToken.None);
        var executed = f.Controls.Read(executedContext, executedContext.EffectId!);
        var executedClaim = f.Controls.Claim(executed.Id, executed.SessionId, Fixture.Id()).Request;
        f.Controls.Complete(executed.Id, executed.SessionId, executedClaim.ClaimId!, "completed", actual, null);
        var executedResult = await executing;
        using (var document = JsonDocument.Parse(executedResult.Content))
            Check(executedResult.Success && executedResult.Evidence?.RequestId == executed.Id &&
                  document.RootElement.GetProperty("receipt").GetString() == executed.Id &&
                  document.RootElement.GetProperty("presentationId").GetString() == presentation.Id &&
                  document.RootElement.GetProperty("mountId").GetString() == f.MountId &&
                  document.RootElement.GetProperty("status").GetString() == "completed" &&
                  document.RootElement.GetProperty("operation").GetString() == "invoke" &&
                  document.RootElement.GetProperty("receipt").GetString() != presentation.Id &&
                  document.RootElement.GetProperty("observation").GetProperty("clickDelivered").GetBoolean() &&
                  !document.RootElement.TryGetProperty("requestId", out _) &&
                  !document.RootElement.TryGetProperty("generation", out _) &&
                  !document.RootElement.TryGetProperty("plugin", out _) &&
                  !document.RootElement.TryGetProperty("claimId", out _) &&
                  !document.RootElement.TryGetProperty("manifestPath", out _),
                "tool wait echoes exact admitted presentation and mount coordinates beside its distinct effect receipt");

        subscription = f.Events.Subscribe();
        var recovered = await f.Tool.ReconcileAsync(f.Call(presentation.Id, "invoke"), executedContext,
            executedResult.Evidence!, CancellationToken.None);
        Check(recovered.Success && recovered.Evidence?.RequestId == executed.Id && !subscription.Reader.TryRead(out _),
            "completed-effect recovery reads its exact receipt without dispatch or a new event");
        f.Events.Unsubscribe(subscription.Id);
        var wrongEvidence = await f.Tool.ReconcileAsync(f.Call(presentation.Id, "invoke"), executedContext,
            executedResult.Evidence! with { ProviderId = "another-provider" }, CancellationToken.None);
        var wrongInput = await f.Tool.ReconcileAsync(f.Call(presentation.Id, "inspect"), executedContext,
            executedResult.Evidence!, CancellationToken.None);
        Check(wrongEvidence.EffectiveOutcome == ToolExecutionOutcome.Unknown &&
              wrongInput.EffectiveOutcome == ToolExecutionOutcome.Unknown &&
              f.Controls.Read(executedContext, executed.Id).Status == "completed",
            "recovery rejects another provider or altered input without changing or redispatching the saved effect");
        var recoveryPending = f.NewControl("invoke");
        var recoveryPendingResult = await f.Tool.ReconcileAsync(f.Call(recoveryPending.Presentation.Id, "invoke"),
            recoveryPending.Context, new ToolExecutionEvidence(ToolExecutionOutcome.Unknown, PluginControlToolExtension.Provider,
                RequestId: recoveryPending.Request.Id), CancellationToken.None);
        Check(recoveryPendingResult.EffectiveOutcome == ToolExecutionOutcome.Rejected &&
              !f.Controls.Claim(recoveryPending.Request.Id, recoveryPending.Context.SessionId, Fixture.Id()).Claimed,
            "recovery retires a retained unclaimed effect instead of delivering it after reconnection");
        var recoveryClaimed = f.NewControl("invoke");
        f.Controls.Claim(recoveryClaimed.Request.Id, recoveryClaimed.Context.SessionId, Fixture.Id());
        var recoveryClaimedResult = await f.Tool.ReconcileAsync(f.Call(recoveryClaimed.Presentation.Id, "invoke"),
            recoveryClaimed.Context, new ToolExecutionEvidence(ToolExecutionOutcome.Unknown, PluginControlToolExtension.Provider,
                RequestId: recoveryClaimed.Request.Id), CancellationToken.None);
        Check(recoveryClaimedResult.EffectiveOutcome == ToolExecutionOutcome.Unknown &&
              recoveryClaimedResult.Evidence?.RequestId == recoveryClaimed.Request.Id &&
              !f.Controls.Claim(recoveryClaimed.Request.Id, recoveryClaimed.Context.SessionId, Fixture.Id()).Claimed,
            "claimed-effect recovery remains Unknown with its original receipt and cannot replay the click");
        var revokedContext = f.Context();
        var revokedTask = f.Tool.ExecuteAsync(f.Call(presentation.Id), revokedContext, CancellationToken.None);
        current = Copy(f.Profile); current.AllowedTools.Clear(); f.Store.Put("agent", current.Id, current);
        var revokedResult = await revokedTask;
        Check(revokedResult.EffectiveOutcome == ToolExecutionOutcome.Unknown &&
              revokedResult.Evidence?.RequestId == revokedContext.EffectId,
            "grant revocation after queueing returns unconfirmed evidence for the exact existing effect");
        f.RestoreProfile();

        using (var limit = new Fixture())
        {
            var limitContext = limit.Context(); var limitPresentation = limit.Present(limitContext);
            var queued = Enumerable.Range(0, 8).Select(_ => limit.Controls.Create(limit.Context(), limitPresentation.Id,
                "inspect", limit.MountId, null)).ToArray();
            Check(Rejects(() => limit.Controls.Create(limit.Context(), limitPresentation.Id, "inspect", limit.MountId, null)),
                "a conversation cannot accumulate more than eight unfinished control effects");
            limit.Controls.Claim(queued[0].Id, queued[0].SessionId, Fixture.Id());
            foreach (var item in queued) limit.Expire(limit.Store.Get<PluginControlRequest>("plugin-control", item.Id)!);
            var fresh = limit.Controls.Create(limit.Context(), limitPresentation.Id, "inspect", limit.MountId, null);
            Check(fresh.Status == "pending" &&
                  limit.Store.Get<PluginControlRequest>("plugin-control", queued[0].Id)!.Status == "uncertain" &&
                  queued.Skip(1).All(x => limit.Store.Get<PluginControlRequest>("plugin-control", x.Id)!.Status == "rejected") &&
                  !limit.Controls.Claim(queued[0].Id, queued[0].SessionId, Fixture.Id()).Claimed,
                "new admission retires expired backlog honestly without replaying claimed effects");
        }
        await RunExpectedTextAssertionChecksAsync(Check, Rejects);
        await RunInvokeExpectedTextAssertionChecksAsync(Check, Rejects);
        await RunTextEditChecksAsync(Check, Rejects);
        await RunReadinessChecksAsync(Check, Rejects);
        await RunFingerprintChecksAsync(Check, Rejects);
        Console.WriteLine($"Plugin control SQLite/metadata fixture checks passed: {checks}. No native plugin was loaded or clicked.");
    }

    private static async Task RunExpectedTextAssertionChecksAsync(Action<bool, string> check, Func<Action, bool> rejects)
    {
        check(PluginControlAssertions.CopyExpectedTexts("inspect", null) is null &&
              PluginControlAssertions.SameExpectedTexts(null, null) &&
              !PluginControlAssertions.SameExpectedTexts(null, ["Count: 1"]),
            "absent expected text remains backward-compatible lifecycle-only input");
        var supplied = new[] { "Count: 1", "Line one\nLine two" };
        var copied = PluginControlAssertions.CopyExpectedTexts("reopen", supplied)!;
        supplied[0] = "mutated caller value";
        check(copied.SequenceEqual(new[] { "Count: 1", "Line one\nLine two" }, StringComparer.Ordinal) &&
              PluginControlAssertions.SameExpectedTexts(copied, ["Count: 1", "Line one\nLine two"]) &&
              !PluginControlAssertions.SameExpectedTexts(copied, ["Line one\nLine two", "Count: 1"]),
            "expected text validation returns a detached exact ordinal list without normalizing ordinary newlines");
        var excessiveUtf8 = Enumerable.Range(0, 3)
            .Select(index => new string('\u754c', 255) + index.ToString()).ToArray();
        check(rejects(() => PluginControlAssertions.CopyExpectedTexts("inspect", ["Count: 1"])) &&
              rejects(() => PluginControlAssertions.CopyExpectedTexts("reopen", [])) &&
              rejects(() => PluginControlAssertions.CopyExpectedTexts("reopen", Enumerable.Repeat("x", 9).ToArray())) &&
              rejects(() => PluginControlAssertions.CopyExpectedTexts("reopen", ["same", "same"])) &&
              rejects(() => PluginControlAssertions.CopyExpectedTexts("reopen", [""])) &&
              rejects(() => PluginControlAssertions.CopyExpectedTexts("reopen", [new string('x', 257)])) &&
              rejects(() => PluginControlAssertions.CopyExpectedTexts("reopen", ["bad\0text"])) &&
              rejects(() => PluginControlAssertions.CopyExpectedTexts("reopen", excessiveUtf8)),
            "expected text validation enforces operation applicability, count, uniqueness, character, NUL and aggregate UTF-8 bounds");

        using (var durable = new Fixture())
        {
            var context = durable.Context();
            var durablePresentation = durable.Present(context);
            var callerList = new List<string> { "Count: 1" };
            var request = durable.Controls.Create(context, durablePresentation.Id, "reopen", durable.MountId, null, callerList);
            callerList[0] = "changed outside request";
            var fromDisk = new StateStore(durable.Store.DirectoryPath)
                .Get<PluginControlRequest>("plugin-control", request.Id)!;
            check(request.ExpectedTexts!.SequenceEqual(["Count: 1"]) &&
                  fromDisk.ExpectedTexts!.SequenceEqual(["Count: 1"]) &&
                  durable.Controls.Create(context, durablePresentation.Id, "reopen", durable.MountId, null,
                      ["Count: 1"]).Id == request.Id,
                "control admission persists a detached expectation and accepts only an exact idempotent retry");
            check(rejects(() => durable.Controls.Create(context, durablePresentation.Id, "reopen", durable.MountId, null)) &&
                  rejects(() => durable.Controls.Create(context, durablePresentation.Id, "reopen", durable.MountId, null,
                      ["Count: 2"])) &&
                  rejects(() => durable.Controls.Create(durable.Context(), durablePresentation.Id, "inspect", durable.MountId,
                      null, ["Count: 1"])),
                "missing, changed, or operation-inapplicable assertions cannot collide with a retained control effect");
            fromDisk.ExpectedTexts![0] = "";
            durable.Store.Put("plugin-control", fromDisk.Id, fromDisk);
            check(rejects(() => durable.Controls.Read(context, request.Id)),
                "malformed retained assertion state fails closed when its durable receipt is read");
        }

        using var f = new Fixture();
        var presentationContext = f.Context();
        var presentation = f.Present(presentationContext);
        PluginControlObservation Observation(IReadOnlyList<string> beforeTexts, IReadOnlyList<string> afterTexts,
            bool beforeLimited = false, bool afterLimited = false) => new(
            new PluginControlSnapshot(f.MountId, beforeTexts, [], beforeLimited),
            new PluginControlSnapshot(f.MountId, afterTexts, [], afterLimited),
            InstanceRecreated: true);
        async Task<(ToolExecutionContext Context, ToolCall Call, PluginControlRequest Request, ToolResult Result)> Run(
            IReadOnlyList<string>? expectedTexts, PluginControlObservation observation, bool legacyPolicy = false)
        {
            var context = f.Context();
            var call = f.Call(presentation.Id, "reopen", expectedTexts);
            var execution = f.Tool.ExecuteAsync(call, context, CancellationToken.None);
            var request = f.Controls.Read(context, context.EffectId!);
            if (legacyPolicy)
            {
                // Restore the absent marker emitted by the original host, before native delivery.
                var legacy = System.Text.Json.Nodes.JsonNode.Parse(CerebralJson.Element(request).GetRawText())!.AsObject();
                legacy.Remove("assertionPolicyVersion");
                f.Store.Put("plugin-control", request.Id, CerebralJson.Read<PluginControlRequest>(CerebralJson.Element(legacy)));
                request = f.Controls.Read(context, request.Id);
            }
            var claim = f.Controls.Claim(request.Id, request.SessionId, Fixture.Id()).Request;
            f.Controls.Complete(request.Id, request.SessionId, claim.ClaimId!, "completed", observation, null);
            return (context, call, request, await execution);
        }

        var passed = await Run(["Count: 1"], Observation(["Count: 1"], ["Count: 1"]));
        using (var document = JsonDocument.Parse(passed.Result.Content))
        {
            var root = document.RootElement;
            var assertion = root.GetProperty("assertion");
            check(passed.Result.Success && passed.Result.EffectiveOutcome == ToolExecutionOutcome.Succeeded &&
                  root.GetProperty("status").GetString() == "completed" &&
                  root.GetProperty("nativeStatus").GetString() == "completed" &&
                  assertion.GetProperty("status").GetString() == "passed" &&
                  assertion.GetProperty("expectedTexts").EnumerateArray().Single().GetString() == "Count: 1" &&
                  assertion.GetProperty("missingTexts").GetArrayLength() == 0 &&
                  f.Controls.Read(passed.Context, passed.Request.Id).Status == "completed",
                "a complete reopen passes only when every exact assertion is visible before and after the new instance");
        }

        var failed = await Run(["Count: 1"], Observation(["Count: 1"], ["Count: 0"]));
        using (var document = JsonDocument.Parse(failed.Result.Content))
        {
            var root = document.RootElement;
            var assertion = root.GetProperty("assertion");
            check(!failed.Result.Success && failed.Result.EffectiveOutcome == ToolExecutionOutcome.Failed &&
                  failed.Result.Evidence is { State: "failed", RequestId: not null } &&
                  root.GetProperty("status").GetString() == "failed" &&
                  root.GetProperty("nativeStatus").GetString() == "completed" &&
                  assertion.GetProperty("status").GetString() == "failed" &&
                  assertion.GetProperty("missingTexts").EnumerateArray().Single().GetString() == "Count: 1" &&
                  root.GetProperty("error").GetString()!.Contains("missing expected text", StringComparison.Ordinal) &&
                  root.GetProperty("observation").GetProperty("instanceRecreated").GetBoolean() &&
                  f.Controls.Read(failed.Context, failed.Request.Id).Status == "completed",
                "a missing post-reopen value fails the assertion while retaining the delivered completed observation and receipt");
        }
        var subscription = f.Events.Subscribe();
        var recovered = await f.Tool.ReconcileAsync(failed.Call, failed.Context, failed.Result.Evidence!, CancellationToken.None);
        var weaker = await f.Tool.ReconcileAsync(f.Call(presentation.Id, "reopen"), failed.Context,
            failed.Result.Evidence!, CancellationToken.None);
        var changed = await f.Tool.ReconcileAsync(f.Call(presentation.Id, "reopen", ["Count: 2"]), failed.Context,
            failed.Result.Evidence!, CancellationToken.None);
        check(recovered.EffectiveOutcome == ToolExecutionOutcome.Failed &&
              weaker.EffectiveOutcome == ToolExecutionOutcome.Unknown && changed.EffectiveOutcome == ToolExecutionOutcome.Unknown &&
              f.Controls.Read(failed.Context, failed.Request.Id).Status == "completed" &&
              !subscription.Reader.TryRead(out _),
            "assertion recovery is read-only and exact; absent or changed expectations cannot weaken the retained failure");
        f.Events.Unsubscribe(subscription.Id);

        var missingBefore = await Run(["Count: 1"], Observation(["Count: 0"], ["Count: 0"]));
        using (var document = JsonDocument.Parse(missingBefore.Result.Content))
            check(!missingBefore.Result.Success && missingBefore.Result.EffectiveOutcome == ToolExecutionOutcome.Failed &&
                  missingBefore.Result.Evidence is { State: "uncertain" } &&
                  document.RootElement.GetProperty("status").GetString() == "uncertain" &&
                  document.RootElement.GetProperty("nativeStatus").GetString() == "completed" &&
                  document.RootElement.GetProperty("assertion").GetProperty("status").GetString() == "inconclusive" &&
                  document.RootElement.GetProperty("assertion").GetProperty("missingTexts")
                      .EnumerateArray().Single().GetString() == "Count: 1",
                "an expectation absent before reopen is inconclusive rather than fabricated persistence evidence");
        var limited = await Run(["Count: 1"], Observation(["Count: 1"], ["Count: 1"], afterLimited: true));
        using (var document = JsonDocument.Parse(limited.Result.Content))
            check(!limited.Result.Success && limited.Result.EffectiveOutcome == ToolExecutionOutcome.Failed &&
                  limited.Result.Evidence is { State: "uncertain" } &&
                  document.RootElement.GetProperty("assertion").GetProperty("status").GetString() == "inconclusive",
                "a limited native snapshot cannot satisfy an exact expected-text assertion");
        var lifecycleOnly = await Run(null, Observation(["Count: 1"], ["Count: 0"]));
        using (var document = JsonDocument.Parse(lifecycleOnly.Result.Content))
            check(lifecycleOnly.Result.Success && lifecycleOnly.Result.EffectiveOutcome == ToolExecutionOutcome.Succeeded &&
                  document.RootElement.GetProperty("status").GetString() == "completed" &&
                  !document.RootElement.TryGetProperty("assertion", out _),
                "a legacy reopen without expectations remains lifecycle evidence and makes no semantic assertion");
        var editorFieldId = Fixture.Id();
        var recreatedEditorFieldId = Fixture.Id();
        PluginControlObservation EditorObservation(string before, string after, bool limited = false,
            bool afterVisible = true) => new(
            new PluginControlSnapshot(f.MountId, [], [], limited)
                { Fields = [new(editorFieldId, before, true, true, false, limited)] },
            new PluginControlSnapshot(f.MountId, [], [], limited)
                { Fields = [new(recreatedEditorFieldId, after, true, afterVisible, false, limited)] },
            InstanceRecreated: true);
        var editorPassed = await Run(["Line one\n世界"], EditorObservation("Line one\n世界", "Line one\n世界"));
        var editorLimited = await Run(["Line one\n世界"], EditorObservation("Line one\n世界", "Line one\n世界", limited: true));
        var editorHidden = await Run(["Line one\n世界"], EditorObservation("Line one\n世界", "Line one\n世界",
            afterVisible: false));
        check(editorPassed.Result.Success && editorPassed.Result.EffectiveOutcome == ToolExecutionOutcome.Succeeded &&
              editorLimited.Result.EffectiveOutcome == ToolExecutionOutcome.Failed &&
              editorHidden.Result.EffectiveOutcome == ToolExecutionOutcome.Failed,
            "reopen assertions accept exact visible unlimited editor values and reject clipped or hidden field evidence");

        check(!rejects(() => PluginControlAssertions.ValidatePolicyVersion(null)) &&
              !rejects(() => PluginControlAssertions.ValidatePolicyVersion(PluginControlAssertions.CurrentPolicyVersion)) &&
              rejects(() => PluginControlAssertions.ValidatePolicyVersion(0)) &&
              rejects(() => PluginControlAssertions.ValidatePolicyVersion(2)),
            "assertion admission supports only the current frozen policy and absent legacy policy");
        check(passed.Request.AssertionPolicyVersion == PluginControlAssertions.CurrentPolicyVersion &&
              new StateStore(f.Store.DirectoryPath).Get<PluginControlRequest>("plugin-control", passed.Request.Id)!.AssertionPolicyVersion ==
                  PluginControlAssertions.CurrentPolicyVersion,
            "new native controls persist their host-owned assertion policy before delivery");

        PluginControlObservation FieldsObservation(string before, string after, string[] labels,
            bool beforeEnabled = true, bool afterEnabled = true, bool beforeReadOnly = false,
            bool afterReadOnly = false, bool beforeVisible = true, bool afterVisible = true,
            bool beforeFieldLimited = false, bool afterFieldLimited = false) => new(
            new PluginControlSnapshot(f.MountId, labels, [])
                { Fields = [new(Fixture.Id(), before, beforeEnabled, beforeVisible, beforeReadOnly, beforeFieldLimited)] },
            new PluginControlSnapshot(f.MountId, labels, [])
                { Fields = [new(Fixture.Id(), after, afterEnabled, afterVisible, afterReadOnly, afterFieldLimited)] },
            InstanceRecreated: true);
        const string probe = "Changed note\n世界";
        var labels = new[] { "Trail Note", "Ready", "Save", "Reload" };
        var staticOnly = await Run(labels, FieldsObservation(new string('n', 97), "", labels));
        using (var document = JsonDocument.Parse(staticOnly.Result.Content))
        {
            var root = document.RootElement;
            var assertion = root.GetProperty("assertion");
            check(!staticOnly.Result.Success && staticOnly.Result.Evidence is { State: "uncertain" } &&
                  root.GetProperty("nativeStatus").GetString() == "completed" &&
                  assertion.GetProperty("status").GetString() == "inconclusive" &&
                  assertion.GetProperty("message").GetString()!.Contains("Static labels", StringComparison.Ordinal) &&
                  assertion.GetProperty("editableFieldPreservation").GetProperty("status").GetString() == "unverified" &&
                  assertion.GetProperty("editableFieldPreservation").GetProperty("witnesses").GetArrayLength() == 0 &&
                  f.Controls.Read(staticOnly.Context, staticOnly.Request.Id).Status == "completed",
                "static labels cannot turn a lost editable note into persistence proof while native completion remains truthful");
        }
        var defaultStaticOnly = await Run(labels, FieldsObservation("", "", labels));
        check(!defaultStaticOnly.Result.Success && defaultStaticOnly.Result.Evidence is { State: "uncertain" },
            "repeating static expectations on an empty default editor cannot manufacture a later persistence pass");
        var actualEditablePreservation = await Run([probe], FieldsObservation(probe, probe, labels));
        using (var document = JsonDocument.Parse(actualEditablePreservation.Result.Content))
        {
            var assertion = document.RootElement.GetProperty("assertion");
            var witness = assertion.GetProperty("editableFieldPreservation").GetProperty("witnesses").EnumerateArray().Single();
            var observation = actualEditablePreservation.Request;
            var retained = f.Controls.Read(actualEditablePreservation.Context, observation.Id).Observation!;
            check(actualEditablePreservation.Result.Success &&
                  assertion.GetProperty("policyVersion").GetInt32() == PluginControlAssertions.CurrentPolicyVersion &&
                  assertion.GetProperty("editableFieldPreservation").GetProperty("status").GetString() == "observed" &&
                  witness.GetProperty("beforeFieldId").GetString() == retained.Before.Fields.Single().Id &&
                  witness.GetProperty("afterFieldId").GetString() == retained.After!.Fields.Single().Id &&
                  witness.GetProperty("beforeFieldId").GetString() != witness.GetProperty("afterFieldId").GetString() &&
                  witness.GetProperty("sha256").GetString() ==
                      Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(probe))).ToLowerInvariant() &&
                  witness.GetProperty("utf8Bytes").GetInt32() == System.Text.Encoding.UTF8.GetByteCount(probe) &&
                  !witness.TryGetProperty("text", out _) &&
                  !witness.TryGetProperty("value", out _),
                "actual nonempty Unicode editor values create bounded factual witnesses across fresh field identities");
        }
        var reusedFieldId = Fixture.Id();
        var reusedFieldObservation = FieldsObservation(probe, probe, labels);
        var reusedFieldIdentity = await Run([probe], reusedFieldObservation with
        {
            Before = reusedFieldObservation.Before with { Fields = [new(reusedFieldId, probe, true, true, false)] },
            After = reusedFieldObservation.After! with { Fields = [new(reusedFieldId, probe, true, true, false)] }
        });
        check(!reusedFieldIdentity.Result.Success && reusedFieldIdentity.Result.Evidence is { State: "uncertain" },
            "a recreated editable view needs fresh actual field identities rather than reusing an old live field");
        var lostEditableValue = await Run([probe], FieldsObservation(probe, "", labels));
        check(!lostEditableValue.Result.Success && lostEditableValue.Result.Evidence is { State: "failed" },
            "a declared editor value lost by the actual recreated view remains a failed postcondition");
        var labelSubstitution = await Run([probe], FieldsObservation(probe, "", [.. labels, probe]));
        check(!labelSubstitution.Result.Success && labelSubstitution.Result.Evidence is { State: "uncertain" },
            "a matching static label cannot substitute for the declared editable after-value");
        var identicalLabelCollision = await Run(["Ready"], FieldsObservation("Ready", "", labels));
        check(!identicalLabelCollision.Result.Success && identicalLabelCollision.Result.Evidence is { State: "uncertain" },
            "a before-field value equal to a static label still requires an actual matching editable after-field");

        foreach (var (reason, observed) in new[]
        {
            ("disabled before-field", FieldsObservation(probe, probe, [.. labels, probe], beforeEnabled: false)),
            ("disabled after-field", FieldsObservation(probe, probe, labels, afterEnabled: false)),
            ("read-only after-field", FieldsObservation(probe, probe, labels, afterReadOnly: true)),
            ("limited before-field", FieldsObservation(probe, probe, [.. labels, probe], beforeFieldLimited: true)),
            ("limited after-field", FieldsObservation(probe, probe, [.. labels, probe], afterFieldLimited: true))
        })
        {
            var unavailableWitness = await Run([probe], observed);
            check(!unavailableWitness.Result.Success && unavailableWitness.Result.Evidence is { State: "uncertain" },
                "editable-data preservation is inconclusive with " + reason);
        }
        var hiddenEditableAfter = await Run([probe], FieldsObservation(probe, probe, [.. labels, probe], afterVisible: false));
        check(!hiddenEditableAfter.Result.Success && hiddenEditableAfter.Result.Evidence is { State: "uncertain" },
            "hidden after-field text cannot satisfy editable-data preservation even when a label repeats it");
        var readonlyVisibleData = await Run([probe], FieldsObservation(probe, probe, [], beforeReadOnly: true, afterReadOnly: true));
        using (var document = JsonDocument.Parse(readonlyVisibleData.Result.Content))
            check(readonlyVisibleData.Result.Success &&
                  document.RootElement.GetProperty("assertion").GetProperty("editableFieldPreservation").GetProperty("status").GetString() == "not-applicable" &&
                  document.RootElement.GetProperty("assertion").GetProperty("editableFieldPreservation").GetProperty("witnesses").GetArrayLength() == 0,
                "read-only views keep exact visible-data assertions without claiming editable-data persistence");
        var editableLifecycle = await Run(null, FieldsObservation(probe, "", labels));
        using (var document = JsonDocument.Parse(editableLifecycle.Result.Content))
            check(editableLifecycle.Result.Success && !document.RootElement.TryGetProperty("assertion", out _),
                "unasserted editor reopen remains lifecycle-only and imposes no persistence requirement");

        var legacyStatic = await Run(labels, FieldsObservation(new string('n', 97), "", labels), legacyPolicy: true);
        using (var document = JsonDocument.Parse(legacyStatic.Result.Content))
            check(legacyStatic.Result.Success && legacyStatic.Request.AssertionPolicyVersion is null &&
                  document.RootElement.GetProperty("assertion").GetProperty("status").GetString() == "passed" &&
                  !document.RootElement.GetProperty("assertion").TryGetProperty("policyVersion", out _) &&
                  !document.RootElement.GetProperty("assertion").TryGetProperty("editableFieldPreservation", out _),
                "absent policy markers retain the original visible-text-only receipt semantics without invented witnesses");
        var legacySubscription = f.Events.Subscribe();
        var legacyReadback = await f.Tool.ReconcileAsync(legacyStatic.Call, legacyStatic.Context, legacyStatic.Result.Evidence!, CancellationToken.None);
        var legacyRetry = f.Controls.Create(legacyStatic.Context, presentation.Id, "reopen", f.MountId, null, labels);
        check(legacyReadback.Success && legacyReadback.Content == legacyStatic.Result.Content &&
              legacyRetry.AssertionPolicyVersion is null && !legacySubscription.Reader.TryRead(out _),
            "legacy exact receipt recovery and idempotent Create neither stamp a new policy nor replay or reinterpret the effect");
        f.Events.Unsubscribe(legacySubscription.Id);

        var witnessSubscription = f.Events.Subscribe();
        var witnessReadback = await f.Tool.ReconcileAsync(actualEditablePreservation.Call, actualEditablePreservation.Context,
            actualEditablePreservation.Result.Evidence!, CancellationToken.None);
        check(witnessReadback.Success && witnessReadback.Content == actualEditablePreservation.Result.Content &&
              !witnessSubscription.Reader.TryRead(out _),
            "new-policy witness recovery remains deterministic and read-only");
        f.Events.Unsubscribe(witnessSubscription.Id);

        var unsupportedContext = f.Context();
        var unsupportedRequest = f.Controls.Create(unsupportedContext, presentation.Id, "reopen", f.MountId, null, labels);
        var unsupportedSaved = System.Text.Json.Nodes.JsonNode.Parse(CerebralJson.Element(unsupportedRequest).GetRawText())!.AsObject();
        unsupportedSaved["assertionPolicyVersion"] = 2;
        f.Store.Put("plugin-control", unsupportedRequest.Id, CerebralJson.Read<PluginControlRequest>(CerebralJson.Element(unsupportedSaved)));
        var unsupportedSubscription = f.Events.Subscribe();
        check(rejects(() => f.Controls.Read(unsupportedContext, unsupportedRequest.Id)) &&
              rejects(() => f.Controls.Create(unsupportedContext, presentation.Id, "reopen", f.MountId, null, labels)) &&
              rejects(() => f.Controls.Claim(unsupportedRequest.Id, unsupportedContext.SessionId, Fixture.Id())) &&
              f.Store.Get<PluginControlRequest>("plugin-control", unsupportedRequest.Id)!.Status == "pending" &&
              !unsupportedSubscription.Reader.TryRead(out _),
            "unsupported retained assertion policies fail closed before owner claim, republishing or native delivery");
        var unsupportedExecution = await f.Tool.ExecuteAsync(f.Call(presentation.Id, "reopen", labels), unsupportedContext, CancellationToken.None);
        var unsupportedRecovery = await f.Tool.ReconcileAsync(f.Call(presentation.Id, "reopen", labels), unsupportedContext,
            new(ToolExecutionOutcome.Unknown, PluginControlToolExtension.Provider, RequestId: unsupportedRequest.Id), CancellationToken.None);
        check(unsupportedExecution.EffectiveOutcome == ToolExecutionOutcome.Rejected &&
              unsupportedRecovery.EffectiveOutcome == ToolExecutionOutcome.Unknown &&
              unsupportedRecovery.Evidence?.RequestId == unsupportedRequest.Id &&
              !unsupportedSubscription.Reader.TryRead(out _),
            "unsupported policies reject dispatch and retain original uncertain evidence during recovery without replay");
        f.Events.Unsubscribe(unsupportedSubscription.Id);

        var rejectedCount = f.Store.List<PluginControlRequest>("plugin-control").Count;
        foreach (var call in new[]
        {
            f.Call(presentation.Id, "inspect", ["Count: 1"]),
            f.Call(presentation.Id, "reopen", ["same", "same"]),
            f.Call(presentation.Id, "reopen", [new string('x', 257)]),
            new ToolCall(PluginControlToolExtension.ToolName, CerebralJson.Element(new
                { presentationId = presentation.Id, operation = "reopen", mountId = f.MountId,
                  expectedTexts = labels, assertionPolicyVersion = 0 }))
        })
        {
            var result = await f.Tool.ExecuteAsync(call, f.Context(), CancellationToken.None);
            check(result.EffectiveOutcome == ToolExecutionOutcome.Rejected,
                "invalid expected text input is rejected before native control admission");
        }
        using (var duplicate = JsonDocument.Parse($$"""
            {"presentationId":"{{presentation.Id}}","operation":"reopen","mountId":"{{f.MountId}}","expectedTexts":["Count: 1"],"expectedTexts":["Count: 2"]}
            """))
        {
            var result = await f.Tool.ExecuteAsync(new ToolCall(PluginControlToolExtension.ToolName,
                duplicate.RootElement.Clone()), f.Context(), CancellationToken.None);
            check(result.EffectiveOutcome == ToolExecutionOutcome.Rejected,
                "duplicate expectedTexts properties are rejected instead of selecting one ambiguous assertion");
        }
        check(f.Store.List<PluginControlRequest>("plugin-control").Count == rejectedCount,
            "invalid expected text input persists no control request");
    }

    private static async Task RunTextEditChecksAsync(Action<bool, string> check, Func<Action, bool> rejects)
    {
        var fieldId = Fixture.Id();
        var value = "Line one\n世界";
        var copied = PluginControlTextEdits.Copy("setText", fieldId, "", value)!;
        check(copied.FieldId == fieldId && copied.ExpectedText == "" && copied.Text == value &&
              PluginControlTextEdits.Copy("inspect", null, null, null) is null,
            "native text input preserves exact ordinary Unicode and newlines with one opaque field identity");
        check(rejects(() => PluginControlTextEdits.Copy("setText", "field-0", "", value)) &&
              rejects(() => PluginControlTextEdits.Copy("setText", fieldId, "same", "same")) &&
              rejects(() => PluginControlTextEdits.Copy("setText", fieldId, "bad\0text", value)) &&
              rejects(() => PluginControlTextEdits.Copy("setText", fieldId, "", new string('x', PluginControlTextEdits.MaximumCharacters + 1))) &&
              rejects(() => PluginControlTextEdits.Copy("inspect", fieldId, "", value)),
            "native text input rejects fabricated identities, no-ops, NUL, oversize values, and fields on other operations");

        using var f = new Fixture();
        var context = f.Context();
        var presentation = f.Present(context);
        var call = f.TextCall(presentation.Id, fieldId, "", value);
        var execution = f.Tool.ExecuteAsync(call, context, CancellationToken.None);
        var request = f.Controls.Read(context, context.EffectId!);
        var fromDisk = new StateStore(f.Store.DirectoryPath).Get<PluginControlRequest>("plugin-control", request.Id)!;
        check(request.TextEdit == copied && fromDisk.TextEdit == copied &&
              f.Controls.Create(context, presentation.Id, "setText", f.MountId, null, null, copied).Id == request.Id &&
              rejects(() => f.Controls.Create(context, presentation.Id, "setText", f.MountId, null, null,
                  copied with { Text = "different" })),
            "setText admission persists its exact CAS input and idempotent retries cannot change it");

        var before = new PluginControlSnapshot(f.MountId, [], [])
            { Fields = [new(fieldId, "", true, true, false)] };
        var after = new PluginControlSnapshot(f.MountId, [], [])
            { Fields = [new(fieldId, value, true, true, false)] };
        var observation = new PluginControlObservation(before, after) { TextSet = true };
        var claim = f.Controls.Claim(request.Id, request.SessionId, Fixture.Id()).Request;
        f.Controls.Complete(request.Id, request.SessionId, claim.ClaimId!, "completed", observation, null);
        var result = await execution;
        var readable = ToolModelProjection.ReadableProjection(PluginControlToolExtension.ToolName, result.Content);
        check(result.Success && result.EffectiveOutcome == ToolExecutionOutcome.Succeeded &&
              result.Evidence is { State: "completed", RequestId: not null } && readable.Complete &&
              readable.Text.Contains(fieldId, StringComparison.Ordinal) &&
              readable.Text.Contains("text set: True", StringComparison.Ordinal) &&
              readable.Text.Contains("Line one\\n", StringComparison.Ordinal),
            "completed setText reports exact bounded before/after editor evidence separately from click delivery");
        using (var body = JsonDocument.Parse(result.Content))
            check(body.RootElement.GetProperty("installedGeneration").GetString() == request.Plugin.Generation &&
                  body.RootElement.GetProperty("installedManifest").GetString() == request.Plugin.ManifestPath &&
                  body.RootElement.GetProperty("sourceManifest").GetString() == presentation.Path &&
                  readable.Text.Contains(request.Plugin.Generation, StringComparison.Ordinal) &&
                  readable.Text.Contains("Reopen reloads this generation", StringComparison.Ordinal),
                "native control feedback separates the exact approved installed generation from mutable source and replacement admission");
        var reconciled = await f.Tool.ReconcileAsync(call, context, result.Evidence!, CancellationToken.None);
        var changed = await f.Tool.ReconcileAsync(f.TextCall(presentation.Id, fieldId, "", "changed"), context,
            result.Evidence!, CancellationToken.None);
        check(reconciled.Success && changed.EffectiveOutcome == ToolExecutionOutcome.Unknown &&
              f.Store.List<PluginControlRequest>("plugin-control").Count == 1,
            "setText reconciliation reads the exact durable receipt and changed input cannot replay it");

        foreach (var bad in new[]
        {
            new PluginControlObservation(before, after),
            new PluginControlObservation(before, after) { TextSet = true, ClickDelivered = true },
            new PluginControlObservation(before with
                { Fields = [new PluginControlTextField(fieldId, "", true, true, false, true)] }, after) { TextSet = true }
        })
        {
            using var invalid = new Fixture();
            var item = invalid.NewTextControl(fieldId, "", value);
            var invalidClaim = invalid.Controls.Claim(item.Request.Id, item.Request.SessionId, Fixture.Id()).Request;
            check(rejects(() => invalid.Controls.Complete(item.Request.Id, item.Request.SessionId,
                    invalidClaim.ClaimId!, "completed", bad with
                    {
                        Before = bad.Before with { Id = invalid.MountId },
                        After = bad.After! with { Id = invalid.MountId }
                    }, null)),
                "completed setText rejects missing delivery, click claims, or limited preimage evidence");
        }

        await CheckSemanticTextOutcomeAsync("different", limited: false, "failed", ToolExecutionOutcome.Failed,
            "confirmed text delivery with an exact mismatched after-value is a semantic failure without replay");
        await CheckSemanticTextOutcomeAsync("different", limited: true, "uncertain", ToolExecutionOutcome.Failed,
            "confirmed text delivery with a limited after-value is inconclusive and does not replay");

        async Task CheckSemanticTextOutcomeAsync(string actualText, bool limited, string expectedState,
            ToolExecutionOutcome expectedOutcome, string message)
        {
            using var semantic = new Fixture();
            var semanticContext = semantic.Context();
            var semanticPresentation = semantic.Present(semanticContext);
            var semanticCall = semantic.TextCall(semanticPresentation.Id, fieldId, "", value);
            var semanticExecution = semantic.Tool.ExecuteAsync(semanticCall, semanticContext, CancellationToken.None);
            var semanticRequest = semantic.Controls.Read(semanticContext, semanticContext.EffectId!);
            var semanticClaim = semantic.Controls.Claim(semanticRequest.Id, semanticRequest.SessionId, Fixture.Id()).Request;
            var semanticBefore = before with { Id = semantic.MountId };
            var semanticAfter = after with
            {
                Id = semantic.MountId,
                Limited = limited,
                Fields = [new PluginControlTextField(fieldId, actualText, true, true, false, limited)]
            };
            semantic.Controls.Complete(semanticRequest.Id, semanticRequest.SessionId, semanticClaim.ClaimId!,
                "completed", new PluginControlObservation(semanticBefore, semanticAfter) { TextSet = true }, null);
            var semanticResult = await semanticExecution;
            using var body = JsonDocument.Parse(semanticResult.Content);
            var assertion = body.RootElement.GetProperty("assertion");
            check(!semanticResult.Success && semanticResult.EffectiveOutcome == expectedOutcome &&
                  semanticResult.Evidence is { State: var state } && state == expectedState &&
                  body.RootElement.GetProperty("nativeStatus").GetString() == "completed" &&
                  assertion.GetProperty("status").GetString() == (limited ? "inconclusive" : "failed") &&
                  body.RootElement.GetProperty("error").GetString()!.Contains(
                      limited ? "limited" : "did not retain", StringComparison.OrdinalIgnoreCase), message);
            var semanticReconciled = await semantic.Tool.ReconcileAsync(semanticCall, semanticContext,
                semanticResult.Evidence!, CancellationToken.None);
            check(semanticReconciled.EffectiveOutcome == expectedOutcome &&
                  semantic.Store.List<PluginControlRequest>("plugin-control").Count == 1,
                message + " and durable reconciliation does not dispatch it again");
        }

        using (var duplicate = JsonDocument.Parse($$"""
            {"presentationId":"{{presentation.Id}}","operation":"setText","mountId":"{{f.MountId}}","fieldId":"{{fieldId}}","expectedText":"","text":"one","text":"two"}
            """))
        {
            var rejected = await f.Tool.ExecuteAsync(new ToolCall(PluginControlToolExtension.ToolName,
                duplicate.RootElement.Clone()), f.Context(), CancellationToken.None);
            check(rejected.EffectiveOutcome == ToolExecutionOutcome.Rejected,
                "duplicate native text properties are rejected before choosing a mutation value");
        }

        var malformed = new Fixture();
        var malformedItem = malformed.NewTextControl(fieldId, "", value);
        var saved = malformed.Store.Get<PluginControlRequest>("plugin-control", malformedItem.Request.Id)!;
        malformed.Store.Put("plugin-control", saved.Id, withTextEdit(copied with { FieldId = "field-0" }));
        check(rejects(() => malformed.Controls.Read(malformedItem.Context, saved.Id)),
            "malformed retained native text input fails closed when its durable receipt is read");

        PluginControlRequest withTextEdit(PluginControlTextEdit edit) => new()
        {
            Id = saved.Id, PresentationId = saved.PresentationId, SessionId = saved.SessionId,
            ProfileId = saved.ProfileId, JobId = saved.JobId, Operation = saved.Operation, MountId = saved.MountId,
            ButtonName = saved.ButtonName, ExpectedTexts = saved.ExpectedTexts, TextEdit = edit, Plugin = saved.Plugin,
            PackageFingerprint = saved.PackageFingerprint, CreatedAt = saved.CreatedAt, ExpiresAt = saved.ExpiresAt,
            Status = saved.Status, ClaimId = saved.ClaimId, Error = saved.Error, Observation = saved.Observation
        };
    }

    private static async Task RunFingerprintChecksAsync(Action<bool, string> check, Func<Action, bool> rejects)
    {
        using var f = new Fixture();
        var item = f.NewControl("inspect");
        var fingerprint = item.Request.PackageFingerprint!;
        check(fingerprint.Length == 64 && fingerprint.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "package fingerprints use a stable lowercase SHA-256 representation");
        var installedDirectory = Path.GetDirectoryName(f.Plugin.ManifestPath)!;
        var installedAssembly = Path.Combine(installedDirectory, "fixture-plugin.dll");
        var copiedDirectory = Path.Combine(f.Store.DirectoryPath, "relocated-package");
        Directory.CreateDirectory(copiedDirectory);
        var copiedManifest = Path.Combine(copiedDirectory, NativeDesktopPluginPackage.ManifestFileName);
        var copiedAssembly = Path.Combine(copiedDirectory, "fixture-plugin.dll");
        File.Copy(f.Plugin.ManifestPath, copiedManifest); File.Copy(installedAssembly, copiedAssembly);
        File.SetLastWriteTimeUtc(copiedAssembly, DateTime.UtcNow.AddDays(-1));
        check(NativeDesktopPluginPackage.ComputeFingerprint(copiedManifest) == fingerprint,
            "identical relocated package bytes retain the fingerprint regardless of path and timestamp");
        var extraFiles = new[] { "z-dependency.dll", "fixture-plugin.deps.json", "assets/branding.png" };
        foreach (var relative in extraFiles)
        {
            var file = Path.Combine(copiedDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "Fixture " + relative);
        }
        var withAssets = NativeDesktopPluginPackage.ComputeFingerprint(copiedManifest);
        check(withAssets != fingerprint, "additional runtime dependencies and assets participate in package identity");
        var reordered = Path.Combine(f.Store.DirectoryPath, "reordered-package"); Directory.CreateDirectory(reordered);
        foreach (var relative in extraFiles.Reverse())
        {
            var file = Path.Combine(reordered, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.Copy(Path.Combine(copiedDirectory, relative), file);
        }
        File.Copy(copiedAssembly, Path.Combine(reordered, "fixture-plugin.dll"));
        var reorderedManifest = Path.Combine(reordered, NativeDesktopPluginPackage.ManifestFileName);
        File.Copy(copiedManifest, reorderedManifest);
        check(NativeDesktopPluginPackage.ComputeFingerprint(reorderedManifest) == withAssets,
            "normalized ordinal relative filenames produce the same digest independent of creation order and relocation");
        foreach (var relative in extraFiles)
        {
            var file = Path.Combine(copiedDirectory, relative); var before = NativeDesktopPluginPackage.ComputeFingerprint(copiedManifest);
            var contents = File.ReadAllText(file); File.AppendAllText(file, "changed");
            check(NativeDesktopPluginPackage.ComputeFingerprint(copiedManifest) != before,
                "fingerprint detects changed adjacent runtime file " + relative);
            File.WriteAllText(file, contents);
        }
        foreach (var relative in extraFiles) File.Delete(Path.Combine(copiedDirectory, relative));
        File.AppendAllText(copiedManifest, " ");
        var changedManifest = NativeDesktopPluginPackage.ComputeFingerprint(copiedManifest);
        check(changedManifest != fingerprint, "manifest byte changes alter the package fingerprint");
        File.AppendAllText(copiedAssembly, "changed compiled bytes");
        check(NativeDesktopPluginPackage.ComputeFingerprint(copiedManifest) != changedManifest,
            "compiled assembly byte changes independently alter the package fingerprint");
        File.Delete(copiedAssembly);
        check(rejects(() => NativeDesktopPluginPackage.ComputeFingerprint(copiedManifest)),
            "fingerprinting rejects a package whose adjacent assembly is absent");
        File.WriteAllText(copiedAssembly, "Admission fixture only. Never loaded.");
        var manifestText = File.ReadAllText(copiedManifest);
        File.WriteAllText(copiedManifest, manifestText.Replace("fixture-plugin.dll", "../fixture-plugin.dll", StringComparison.Ordinal));
        check(rejects(() => NativeDesktopPluginPackage.ComputeFingerprint(copiedManifest)),
            "fingerprinting retains manifest admission against nonadjacent assembly paths");
        File.WriteAllText(copiedManifest, manifestText);
        File.Delete(copiedAssembly);
        var linkCreated = false;
        try { File.CreateSymbolicLink(copiedAssembly, installedAssembly); linkCreated = true; }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        { Console.WriteLine("SKIP: fingerprint link fixture requires local symbolic-link creation permission."); }
        if (linkCreated)
            check(rejects(() => NativeDesktopPluginPackage.ComputeFingerprint(copiedManifest)),
                "fingerprinting rejects a linked adjacent assembly without loading plugin code");

        var entryLimited = Path.Combine(f.Store.DirectoryPath, "entry-limit-package"); Directory.CreateDirectory(entryLimited);
        var entryManifest = Path.Combine(entryLimited, NativeDesktopPluginPackage.ManifestFileName);
        File.Copy(f.Plugin.ManifestPath, entryManifest); File.Copy(installedAssembly, Path.Combine(entryLimited, "fixture-plugin.dll"));
        for (var index = 0; index < NativeDesktopPluginPackage.MaximumEntries - 1; index++)
            File.WriteAllText(Path.Combine(entryLimited, "asset-" + index + ".txt"), "");
        check(rejects(() => NativeDesktopPluginPackage.ComputeFingerprint(entryManifest)),
            "the shared fingerprint/copy admission retains the existing 2048-file bound");
        var depthLimited = Path.Combine(f.Store.DirectoryPath, "depth-limit-package"); Directory.CreateDirectory(depthLimited);
        var depthManifest = Path.Combine(depthLimited, NativeDesktopPluginPackage.ManifestFileName);
        File.Copy(f.Plugin.ManifestPath, depthManifest); File.Copy(installedAssembly, Path.Combine(depthLimited, "fixture-plugin.dll"));
        var nested = depthLimited;
        for (var depth = 0; depth <= NativeDesktopPluginPackage.MaximumDepth; depth++)
        { nested = Path.Combine(nested, "level"); Directory.CreateDirectory(nested); }
        check(rejects(() => NativeDesktopPluginPackage.ComputeFingerprint(depthManifest)),
            "the shared fingerprint/copy admission retains the existing 16-level bound even for empty directories");

        var installedBytes = File.ReadAllText(installedAssembly);
        var claimDrift = f.NewControl("reopen");
        var readinessDrift = f.NewControl("reopen");
        var readinessClaim = f.Controls.Claim(readinessDrift.Request.Id, readinessDrift.Context.SessionId, Fixture.Id()).Request;
        File.AppendAllText(installedAssembly, "Changed after owner approval.");
        check(!f.Controls.Claim(claimDrift.Request.Id, claimDrift.Context.SessionId, Fixture.Id()).Claimed &&
              f.Controls.Read(claimDrift.Context, claimDrift.Request.Id).Status == "rejected" &&
              rejects(() => f.Controls.Ready(readinessDrift.Request.Id, readinessDrift.Context.SessionId,
                  readinessClaim.ClaimId!, (_, _) => { })),
            "owner claim and readiness recheck exact package bytes before a reopen can reach the desktop");
        var alteredContext = f.Context();
        var altered = await f.Tool.ExecuteAsync(f.Call(item.Presentation.Id), alteredContext, CancellationToken.None);
        check(altered.EffectiveOutcome == ToolExecutionOutcome.Rejected &&
            altered.Content.Contains("changed after owner review", StringComparison.Ordinal) &&
            f.Store.Get<PluginControlRequest>("plugin-control", alteredContext.EffectId!) is null,
            "new native control fails closed before dispatch when installed bytes differ from owner-reviewed package identity");
        check(f.Controls.Create(item.Context, item.Presentation.Id, "inspect", f.MountId, null).PackageFingerprint == fingerprint,
            "exact-effect recovery retains its original control identity without rehashing or replay after package mutation");
        File.WriteAllText(installedAssembly, installedBytes);
        item.Presentation.PackageFingerprint = null; f.Store.Put("interface", item.Presentation.Id, item.Presentation);
        var compatible = f.Controls.Create(f.Context(), item.Presentation.Id, "inspect", f.MountId, null);
        check(compatible.PackageFingerprint == fingerprint,
            "older owner receipts without requested fingerprint retain existing control compatibility without backfilling the presentation");
        item.Presentation.PackageFingerprint = fingerprint; f.Store.Put("interface", item.Presentation.Id, item.Presentation);

        var claim = f.Controls.Claim(item.Request.Id, item.Context.SessionId, Fixture.Id()).Request;
        f.Controls.Complete(item.Request.Id, item.Context.SessionId, claim.ClaimId!, "completed", f.Observation(false), null);
        File.AppendAllText(item.Presentation.Path, "changed mutable source");
        File.AppendAllText(installedAssembly, "changed later installed bytes");
        check(new PluginControlRequests(new StateStore(f.Store.DirectoryPath), f.Events)
                .Read(item.Context, item.Request.Id).PackageFingerprint == fingerprint,
            "retained control fingerprints survive SQLite reopen and later source or installed file changes");
        File.Delete(installedAssembly);
        var reconciled = await f.Tool.ReconcileAsync(f.Call(item.Presentation.Id), item.Context,
            new(ToolExecutionOutcome.Succeeded, PluginControlToolExtension.Provider, RequestId: item.Request.Id), CancellationToken.None);
        using (var document = JsonDocument.Parse(reconciled.Content))
            check(reconciled.Success && document.RootElement.GetProperty("packageFingerprint").GetString() == fingerprint,
                "reconciliation projects the saved fingerprint without reopening mutable package files");
        var existing = f.Store.Get<PluginControlRequest>("plugin-control", item.Request.Id)!;
        f.Store.Put("plugin-control", existing.Id, new PluginControlRequest
        {
            Id = existing.Id, PresentationId = existing.PresentationId, SessionId = existing.SessionId,
            ProfileId = existing.ProfileId, JobId = existing.JobId, Operation = existing.Operation, MountId = existing.MountId,
            Plugin = existing.Plugin, CreatedAt = existing.CreatedAt, ExpiresAt = existing.ExpiresAt,
            Status = existing.Status, ClaimId = existing.ClaimId, Observation = existing.Observation
        });
        var older = await f.Tool.ReconcileAsync(f.Call(item.Presentation.Id), item.Context,
            reconciled.Evidence!, CancellationToken.None);
        using (var document = JsonDocument.Parse(older.Content))
            check(older.Success && !document.RootElement.TryGetProperty("packageFingerprint", out _) &&
                f.Controls.Create(item.Context, item.Presentation.Id, "inspect", f.MountId, null).PackageFingerprint is null,
                "older retained controls remain explicitly without a fingerprint and retries never synthesize one");
        var missingContext = f.Context();
        var missing = await f.Tool.ExecuteAsync(f.Call(item.Presentation.Id), missingContext, CancellationToken.None);
        check(missing.EffectiveOutcome == ToolExecutionOutcome.Rejected &&
            f.Store.Get<PluginControlRequest>("plugin-control", missingContext.EffectId!) is null,
            "new control admission fails closed without creating a request when installed package bytes are unavailable");
    }

    private static async Task RunReadinessChecksAsync(Action<bool, string> check, Func<Action, bool> rejects)
    {
        using var f = new Fixture();
        var item = f.NewControl("invoke");
        var claim = f.Controls.Claim(item.Request.Id, item.Context.SessionId, Fixture.Id()).Request;
        var callbacks = 0;
        void ValidateRuns(InterfaceRequest presentation, PluginControlRequest request)
        {
            if (presentation.Id != item.Presentation.Id || request.Id != item.Request.Id)
                throw new Exception("Readiness callback received foreign retained records.");
            callbacks++;
        }
        var original = CerebralJson.Element(f.Store.Get<PluginControlRequest>("plugin-control", item.Request.Id)!);
        var subscription = f.Events.Subscribe();
        var ready = f.Controls.Ready(item.Request.Id, item.Context.SessionId, claim.ClaimId!, ValidateRuns);
        check(ready.Status == "claimed" && callbacks == 1 &&
              JsonElement.DeepEquals(original, CerebralJson.Element(f.Store.Get<PluginControlRequest>("plugin-control", item.Request.Id)!)) &&
              !subscription.Reader.TryRead(out _),
            "owner readiness reads the exact claimed records without a state transition or another dispatch event");
        f.Events.Unsubscribe(subscription.Id);
        check(rejects(() => f.Controls.Ready(item.Request.Id, item.Context.SessionId, Fixture.Id(), ValidateRuns)) &&
              rejects(() => f.Controls.Ready(item.Request.Id, "foreign-conversation", claim.ClaimId!, ValidateRuns)) &&
              rejects(() => f.Controls.Ready(item.Request.Id, item.Context.SessionId, claim.ClaimId!, null!)) && callbacks == 1,
            "readiness requires the exact owner claim, conversation and a nonoptional host run validator");
        var current = CerebralJson.Read<AgentProfile>(CerebralJson.Element(f.Profile));
        current.AllowedTools.Remove(PluginControlToolExtension.ToolName); f.Store.Put("agent", current.Id, current);
        check(rejects(() => f.Controls.Ready(item.Request.Id, item.Context.SessionId, claim.ClaimId!, ValidateRuns)),
            "current tool revocation after claim blocks readiness before native delivery");
        f.RestoreProfile();
        current = CerebralJson.Read<AgentProfile>(CerebralJson.Element(f.Profile));
        current.Capabilities.Remove("desktop.plugins.manage"); f.Store.Put("agent", current.Id, current);
        check(rejects(() => f.Controls.Ready(item.Request.Id, item.Context.SessionId, claim.ClaimId!, ValidateRuns)),
            "current capability revocation after claim blocks readiness before native delivery");
        f.RestoreProfile();
        item.Presentation.Plugin = item.Presentation.Plugin! with { ControlTestsAllowed = false };
        f.Store.Put("interface", item.Presentation.Id, item.Presentation);
        check(rejects(() => f.Controls.Ready(item.Request.Id, item.Context.SessionId, claim.ClaimId!, ValidateRuns)),
            "owner opt-in revocation after claim blocks readiness");
        item.Presentation.Plugin = f.Plugin with { Generation = "1.0.0-" + Fixture.Id() };
        f.Store.Put("interface", item.Presentation.Id, item.Presentation);
        check(rejects(() => f.Controls.Ready(item.Request.Id, item.Context.SessionId, claim.ClaimId!, ValidateRuns)),
            "changed retained generation after claim blocks readiness");
        item.Presentation.Plugin = f.Plugin; f.Store.Put("interface", item.Presentation.Id, item.Presentation);
        f.Controls.Interrupt(item.Context, item.Request.Id, true);
        check(rejects(() => f.Controls.Ready(item.Request.Id, item.Context.SessionId, claim.ClaimId!, ValidateRuns)) &&
              f.Controls.Complete(item.Request.Id, item.Context.SessionId, claim.ClaimId!, "completed", f.Observation(true), null).Status == "completed",
            "uncertain interrupted claims cannot deliver again while a truthful late delivered observation can still complete");
        var expired = f.NewControl("inspect");
        var expiredClaim = f.Controls.Claim(expired.Request.Id, expired.Context.SessionId, Fixture.Id()).Request;
        f.Expire(expiredClaim);
        check(rejects(() => f.Controls.Ready(expired.Request.Id, expired.Context.SessionId, expiredClaim.ClaimId!, (_, _) => { })) &&
              f.Store.Get<PluginControlRequest>("plugin-control", expired.Request.Id)!.Status == "claimed",
            "readiness rejects a deadline passed after claim without mutating the retained outcome");

        var configuration = new Configuration(f.Store);
        var host = new WorkerWorkflows(f.Store, configuration, f.Events, [], []);
        await using var runtime = new ModelRuntime(f.Store, configuration.Get, f.Events);
        await using var tools = new ToolDispatcher(configuration.Get, [], f.Store.DirectoryPath);
        using var scheduler = new JobScheduler(f.Store, configuration, new NoExecutor(), runtime, f.Events,
            NullLogger<JobScheduler>.Instance, host);
        var commands = new CommandDispatcher(f.Store, configuration, runtime, scheduler, tools, f.Events, workflows: host);
        var unavailable = new CommandDispatcher(f.Store, configuration, runtime, scheduler, tools, f.Events);
        var empty = WorkflowContract.EmptyObject;
        var descriptor = new WorkflowCommandDescriptor("fixture.control", 1, "Retained metadata only; never executed.", empty, empty, []);
        (PluginControlRequest Control, WorkflowRun Run) ActiveControl(WorkflowStatus? sourceStatus = null)
        {
            var sessionId = Fixture.Id();
            // Reuse the complete fixture admission snapshot, including real captured tool
            // descriptors, while each native conversation/run retains its own owner identity.
            var admitted = f.Context();
            var settings = host.Store.Get(admitted.JobId).Submission.SettingsSnapshot;
            WorkflowRun Submit() => host.Store.Submit(new WorkflowSubmission(Fixture.Id(), sessionId, f.Profile.Id,
                ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                "Fixture control preflight only.", null, empty, settings,
                Caller: new(EventActorKind.User, "fixture-owner")));
            InterfaceRequest? presentation = null;
            if (sourceStatus is { } status)
            {
                var source = Submit();
                presentation = f.Present(f.Context() with { SessionId = sessionId, JobId = source.Id });
                if (status == WorkflowStatus.Cancelled) host.Store.Cancel(source.Id);
                else
                {
                    source = host.Store.TryClaim(source.Id)!;
                    host.Store.ApplyStep(source.Id, source.Revision, new(empty, new CompleteWorkflow(empty, false, "Fixture presenting run failed.")));
                }
            }
            var run = host.Store.TryClaim(Submit().Id)!;
            run = host.Store.ApplyStep(run.Id, run.Revision, new(empty, new InvokeWorkflowCommand(descriptor.Id, descriptor.Version, empty)), descriptor);
            run = host.Store.DispatchEffect(run.Id, run.Revision);
            var context = f.Context() with { SessionId = sessionId, JobId = run.Id, EffectId = run.Effect!.Id };
            presentation ??= f.Present(context);
            var control = f.Controls.Create(context, presentation.Id, "invoke", f.MountId, "Increment");
            return (f.Controls.Claim(control.Id, sessionId, Fixture.Id()).Request, run);
        }
        Task<CommandResult> Preflight(CommandDispatcher dispatcher, PluginControlRequest request, string? profileId = null) =>
            dispatcher.ExecuteAsync(new("interface.controls.ready", CerebralJson.Element(new
                { id = request.Id, sessionId = request.SessionId, claimId = request.ClaimId }), profileId));
        var active = ActiveControl();
        check((await Preflight(commands, active.Control)).Success && (await Preflight(commands, active.Control)).Success &&
              f.Store.Get<PluginControlRequest>("plugin-control", active.Control.Id)!.Status == "claimed",
            "owner command preflight reads actual SQLite workflow ownership and its exact live dispatched control effect without replay");
        check(!(await Preflight(unavailable, active.Control)).Success,
            "owner preflight fails closed when the native workflow engine is unavailable");
        current = CerebralJson.Read<AgentProfile>(CerebralJson.Element(f.Profile)); current.Capabilities.Add("administration");
        f.Store.Put("agent", current.Id, current);
        check(!(await Preflight(commands, active.Control, current.Id)).Success,
            "an administration agent cannot access the owner-only control readiness route");
        f.RestoreProfile();
        check(!(await commands.ExecuteAsync(new("interface.controls.ready", CerebralJson.Element(new
                { id = active.Control.Id, sessionId = active.Control.SessionId, claimId = active.Control.ClaimId, ready = true })))).Success,
            "owner readiness rejects caller-selected readiness and expanded transport arguments");
        host.Store.Cancel(active.Run.Id);
        check(!(await Preflight(commands, active.Control)).Success &&
              f.Store.Get<PluginControlRequest>("plugin-control", active.Control.Id)!.Status == "claimed",
            "durable executing-run cancellation blocks delivery even before the waiting tool updates its claimed receipt");
        foreach (var state in new[] { WorkflowStatus.Cancelled, WorkflowStatus.Failed })
        {
            var stoppedSource = ActiveControl(state);
            check(!(await Preflight(commands, stoppedSource.Control)).Success,
                "durable presenting-run " + state.ToString().ToLowerInvariant() + " state blocks delivery in a live later run");
        }
        var unknown = ActiveControl();
        host.Store.CompleteEffect(unknown.Run.Id, unknown.Control.Id,
            new(WorkflowEffectState.Unknown, empty, "Fixture inactive uncertain effect."));
        check(!(await Preflight(commands, unknown.Control)).Success,
            "an inactive uncertain engine effect cannot become a new native invocation through readiness");
    }

    private sealed class NoExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => throw new InvalidOperationException("Control preflight fixtures never execute a model.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Cerebral.PluginControlTests-" + Id()));
        private readonly string project;
        private readonly string sourceManifest;
        public StateStore Store { get; }
        public EventHub Events { get; } = new();
        public AgentProfile Profile { get; }
        public InterfaceRequests Presentations { get; }
        public PluginControlRequests Controls { get; }
        public PluginControlToolExtension Tool { get; }
        public InterfacePluginReceipt Plugin { get; }
        public string SessionId { get; } = Id();
        public string MountId => Plugin.Id + ".counter";
        public static string Id() => Guid.NewGuid().ToString("N");

        public Fixture()
        {
            project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
            sourceManifest = Path.Combine(project, "desktop-plugin.json");
            const string pluginId = "acceptance.control-fixture";
            const string version = "1.0.0";
            var generation = version + "-" + Id();
            var repository = Path.Combine(root, "plugins");
            var installed = Path.Combine(repository, ".packages", pluginId, generation);
            Directory.CreateDirectory(installed);
            var settings = Path.Combine(repository, ".settings"); Directory.CreateDirectory(settings);
            var manifest = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, contract = "pegasus.cerebral.desktop", apiVersion = 1,
                id = pluginId, name = "Control fixture", version, description = "Metadata admission only; never loaded.",
                assembly = "fixture-plugin.dll", entryType = "Acceptance.ControlFixture"
            });
            File.WriteAllText(sourceManifest, manifest);
            File.WriteAllText(Path.Combine(project, "fixture-plugin.dll"), "Admission fixture only. Never loaded.");
            var installedManifest = Path.Combine(installed, "desktop-plugin.json");
            File.WriteAllText(installedManifest, manifest);
            File.WriteAllText(Path.Combine(installed, "fixture-plugin.dll"), "Admission fixture only. Never loaded.");
            File.WriteAllText(Path.Combine(settings, pluginId + ".active.json"),
                JsonSerializer.Serialize(new { Generation = generation }));
            Plugin = new InterfacePluginReceipt(pluginId, version, generation, installedManifest,
                [new InterfacePluginMountReceipt("sidebar", pluginId + ".counter")], ControlTestsAllowed: true);
            Store = new StateStore(Path.Combine(root, "data"));
            Profile = new AgentProfile
            {
                Id = "fixture-lead", Role = "lead", WorkerId = "local", AllowedRoots = [project],
                Capabilities = ["filesystem.read", "process.execute", "desktop.plugins.manage"],
                AllowedTools = ["file.read", "process.run", InterfaceToolExtension.PluginToolName, InterfaceToolExtension.StatusToolName,
                    PluginControlToolExtension.ToolName]
            };
            RestoreProfile();
            Presentations = new InterfaceRequests(Store, Events);
            Controls = new PluginControlRequests(Store, Events);
            Tool = new PluginControlToolExtension(Store, Events);
        }

        public void RestoreProfile() => Store.Put("agent", Profile.Id, Profile);
        public ToolExecutionContext Context() => WorkflowUiOriginTestRuns.Metadata(Store,
            new(Profile, SessionId, Id(), project, EffectId: Id()) { EnforceProfileScope = true });
        public InterfaceRequest Present(ToolExecutionContext context, bool testsAllowed = true,
            bool complete = true, bool includePlugin = true)
        {
            var request = WorkflowUiOriginTestRuns.SeedPresentation(Store, context, "plugin", sourceManifest);
            if (!complete) return request;
            var claim = Presentations.Claim(request.Id, Id());
            if (!claim.Claimed) throw new Exception("Fixture presentation admission failed: " + claim.Request.Error);
            return Presentations.Complete(request.Id, claim.Request.ClaimId!, true,
                message: "Fixture owner reported installation only; no plugin code was executed.",
                plugin: includePlugin ? Plugin with { ControlTestsAllowed = testsAllowed } : null);
        }
        public (ToolExecutionContext Context, InterfaceRequest Presentation, PluginControlRequest Request) NewControl(string operation)
        {
            var context = Context(); var presentation = Present(context);
            var request = Controls.Create(context, presentation.Id, operation, MountId,
                operation == "invoke" ? "Increment" : null);
            return (context, presentation, request);
        }
        public (ToolExecutionContext Context, InterfaceRequest Presentation, PluginControlRequest Request) NewTextControl(
            string fieldId, string expectedText, string text)
        {
            var context = Context(); var presentation = Present(context);
            var edit = PluginControlTextEdits.Copy("setText", fieldId, expectedText, text)!;
            var request = Controls.Create(context, presentation.Id, "setText", MountId, null, null, edit);
            return (context, presentation, request);
        }
        public ToolCall Call(string presentationId, string operation = "inspect",
            IReadOnlyList<string>? expectedTexts = null) => new(PluginControlToolExtension.ToolName,
            operation == "invoke"
                ? expectedTexts is not null
                    ? CerebralJson.Element(new { presentationId, operation, mountId = MountId, buttonName = "Increment", expectedTexts })
                    : CerebralJson.Element(new { presentationId, operation, mountId = MountId, buttonName = "Increment" })
                : expectedTexts is not null
                    ? CerebralJson.Element(new { presentationId, operation, mountId = MountId, expectedTexts })
                    : CerebralJson.Element(new { presentationId, operation, mountId = MountId }));
        public ToolCall TextCall(string presentationId, string fieldId, string expectedText, string text) =>
            new(PluginControlToolExtension.ToolName, CerebralJson.Element(new
                { presentationId, operation = "setText", mountId = MountId, fieldId, expectedText, text }));
        public (ToolExecutionContext Context, InterfaceRequest Presentation) Reviewed()
        {
            var presenting = Context();
            var presentation = Present(presenting);
            var runId = presentation.PluginTests?.RequestId ?? throw new Exception("Fixture did not retain plugin-test consent.");
            var qa = WorkflowUiOriginTestRuns.ApprovedQa(Store, presenting, presentation);
            return (qa, presentation);
        }
        public PluginControlObservation Observation(bool click, bool recreated = false)
        {
            var before = new PluginControlSnapshot(MountId, ["Count: 0"],
                [new PluginControlButton("Increment", true, true)]);
            var after = new PluginControlSnapshot(MountId, [click ? "Count: 1" : "Count: 0"],
                [new PluginControlButton("Increment", true, true)]);
            return new(before, after, click, recreated);
        }
        public void Expire(PluginControlRequest value) => Store.Put("plugin-control", value.Id, new PluginControlRequest
        {
            Id = value.Id, PresentationId = value.PresentationId, SessionId = value.SessionId, ProfileId = value.ProfileId,
            JobId = value.JobId, Operation = value.Operation, MountId = value.MountId, ButtonName = value.ButtonName,
            ExpectedTexts = value.ExpectedTexts, TextEdit = value.TextEdit,
            Plugin = value.Plugin, PackageFingerprint = value.PackageFingerprint,
            CreatedAt = value.CreatedAt, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            Status = value.Status, ClaimId = value.ClaimId, Error = value.Error, Observation = value.Observation
        });
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
