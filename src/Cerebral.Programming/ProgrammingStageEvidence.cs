using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Programming;

public sealed partial class ProgrammingAgentHarness
{
    private sealed record StageProcess(int ExitCode, string Stdout, string Stderr);

    private static StageFileFact ReadStageFile(Input input, HarnessState state, WorkflowEffect effect, ToolData? data, string path)
    {
        if (effect.State != WorkflowEffectState.Succeeded || data is not { Name: "file.read", Truncated: false, FollowUpRequired: false } ||
            data.Evidence is { } evidence && (evidence.Outcome != ToolExecutionOutcome.Succeeded || evidence.Partial == true ||
                evidence.RequestId is not null && evidence.RequestId != effect.Id))
            throw new ArgumentException("A complete successful declared-file read is required.");
        var raw = ProgrammingEvidence.Read(data.Content);
        if (raw.ValueKind != JsonValueKind.Object || !SamePath(ProgrammingEvidence.Text(raw, "path"), path) ||
            !ProgrammingEvidence.IsHash(ProgrammingEvidence.Text(raw, "sha256")) ||
            !raw.TryGetProperty("startLine", out var start) || !start.TryGetInt32(out var first) || first != 1 ||
            !raw.TryGetProperty("totalLines", out var total) || !total.TryGetInt32(out var lines) || lines is < 1 or > 1000 ||
            !raw.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String ||
            Encoding.UTF8.GetByteCount(content.GetString()!) > 4096)
            throw new ArgumentException("The declared file requires a bounded whole-file read with a path and SHA-256.");
        var source = ActiveSources(state).SingleOrDefault(source => source.ReceiptId == effect.Id && SamePath(source.Path, path));
        if (source is null || source.StartLine != 1 || source.VisibleLineCount != lines || source.TotalReadLines != lines || source.ProjectionLimited)
            throw new ArgumentException("The complete declared source body must be present in the active read projection.");
        return new(path, ProgrammingEvidence.Text(raw, "sha256").ToLowerInvariant(), effect.Id);
    }

    private static StageProcess ReadStageProcess(Input input, HarnessState before, WorkflowEffect effect, ToolData? data)
    {
        // Bind outcome classification to the dispatched arguments, never a later expectation.
        // Older pending commands omit this optional field and retain default-zero semantics.
        var expectedExit = 0;
        if (before.PendingArguments!.Value.TryGetProperty("expectedExitCode", out var expected) &&
            (expected.ValueKind != JsonValueKind.Number || !expected.TryGetInt32(out expectedExit) || expectedExit is < 0 or > 255))
            throw new ArgumentException("The saved process expectation must be an integer from 0 to 255.");
        if (before.Stages!.Pending != "baseline-check" && expectedExit != 0)
            throw new ArgumentException("Only a configured baseline check may expect a nonzero child exit.");
        if (data is not { Name: "process.run", Truncated: false, FollowUpRequired: false } ||
            data.Evidence is not { ExitCode: not null } evidence || evidence.Partial == true ||
            evidence.RequestId is not null && evidence.RequestId != effect.Id ||
            !((effect.State == WorkflowEffectState.Succeeded && evidence.Outcome == ToolExecutionOutcome.Succeeded && evidence.ExitCode == expectedExit) ||
              (effect.State == WorkflowEffectState.Failed && evidence.Outcome == ToolExecutionOutcome.Failed && evidence.ExitCode != expectedExit)))
            throw new ArgumentException("A complete confirmed process outcome with matching state/exit/effect is required.");
        if (string.IsNullOrWhiteSpace(evidence.ProviderId) || evidence.ProviderId.Length > 256 || evidence.ProviderId.Any(char.IsControl) ||
            evidence.JobId is { } job && (job.Length is < 1 or > 256 || job.Any(char.IsControl)))
            throw new ArgumentException("Invalid retained process provider identity.");
        if (Encoding.UTF8.GetByteCount(data.Content) > 64 * 1024)
            throw new ArgumentException("The process result exceeds the bounded evidence reader.");
        var raw = ProgrammingEvidence.Read(data.Content);
        if (raw.ValueKind != JsonValueKind.Object) throw new ArgumentException("Unsupported process result envelope.");
        string stdout, stderr;
        if (evidence.ProviderId == "cerebral.process")
        {
            if (evidence.State != "completed" || raw.EnumerateObject().Count() != 3 ||
                StageInt(raw, "exitCode") != evidence.ExitCode)
                throw new ArgumentException("Native process metadata and child exit do not match.");
            stdout = StageText(raw, "stdout"); stderr = StageText(raw, "stderr");
        }
        else
        {
            // Host tool routing owns the MCP provider identity. Recognize only the PAO build envelope,
            // never an arbitrary MCP tool's text or operation exit as the child process exit.
            if (string.IsNullOrWhiteSpace(evidence.ProviderId) || !evidence.ProviderId.StartsWith("mcp.", StringComparison.Ordinal) ||
                before.Stages!.Pending is not ("baseline-build" or "build") || evidence.Partial != false ||
                evidence.RequestId != effect.Id || evidence.JobId is not { Length: 32 } ||
                !evidence.JobId.All(char.IsAsciiHexDigit) ||
                evidence.State is not ("done" or "completed" or "succeeded" or "failed" or "error") ||
                ProgrammingEvidence.Text(raw, "op") != "dotnet_build" ||
                !raw.TryGetProperty("ok", out var ok) || ok.ValueKind != (evidence.ExitCode == 0 ? JsonValueKind.True : JsonValueKind.False))
                throw new ArgumentException("Unsupported PAO build provider/result contract.");
            if (raw.TryGetProperty("rid", out _) != raw.TryGetProperty("jid", out _))
                throw new ArgumentException("PAO body request/job identities must be present or absent together.");
            foreach (var pair in new[] { ("rid", evidence.RequestId), ("jid", evidence.JobId) })
            {
                if (!raw.TryGetProperty(pair.Item1, out var field))
                {
                    if (evidence.ExitCode != 0) throw new ArgumentException("A failed PAO build requires its original request/job joins.");
                }
                else if (field.ValueKind != JsonValueKind.String || field.GetString() != pair.Item2)
                    throw new ArgumentException("PAO request/job identity mismatch.");
            }
            if (effect.ProviderReceiptId is not null && effect.ProviderReceiptId != evidence.JobId)
                throw new ArgumentException("The saved provider receipt does not match the PAO job.");
            if (raw.TryGetProperty("partial", out var partial) && partial.ValueKind != JsonValueKind.False ||
                raw.TryGetProperty("state", out var status) && (status.ValueKind != JsonValueKind.String || status.GetString() != evidence.State))
                throw new ArgumentException("The PAO result is partial or has conflicting state.");
            var child = raw.GetProperty("data");
            if (child.ValueKind != JsonValueKind.Object || StageInt(child, "exit") != evidence.ExitCode)
                throw new ArgumentException("PAO child exit does not match its retained evidence.");
            foreach (var flag in new[] { "out_truncated", "err_truncated", "timed_out" })
                if (!child.TryGetProperty(flag, out var value) || value.ValueKind != JsonValueKind.False)
                    throw new ArgumentException("PAO child streams are incomplete or timed out.");
            stdout = StageText(child, "out"); stderr = StageText(child, "err_tail");
            if (child.TryGetProperty("err", out var err) && (err.ValueKind != JsonValueKind.String || err.GetString() != stderr))
                throw new ArgumentException("PAO stderr projections conflict.");
            if (child.TryGetProperty("tool", out var tool) && (tool.ValueKind != JsonValueKind.String || tool.GetString() != "dotnet"))
                throw new ArgumentException("PAO result names a different executable.");
        }
        return new(evidence.ExitCode.Value, stdout, stderr);
    }

    private static int StageInt(JsonElement value, string property) =>
        value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var result)
            ? result : throw new ArgumentException("Missing integer process field: " + property);
    private static string StageText(JsonElement value, string property) =>
        value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString()! : throw new ArgumentException("Missing string process field: " + property);

    private static bool MatchesStageExpectation(Input input, StageExpectation expected, StageProcess process)
    {
        if (expected.ExitCode != process.ExitCode || expected.Stdout is not null &&
            (process.Stdout != expected.Stdout || process.Stderr != expected.Stderr)) return false;
        if (expected.CompilerDiagnostic is not { } diagnostic) return true;
        // Reuse the supported bounded C# parser with a derived native-shaped view; the original
        // retained effect and its hash are untouched. This does not upgrade unsupported formats.
        var batch = ProgrammingCompilerDiagnostics.Parse(JsonSerializer.Serialize(new {
            exitCode = process.ExitCode, stdout = process.Stdout, stderr = process.Stderr }, Json), process.ExitCode);
        return batch.Complete && batch.Diagnostics.Any(item => item.Code == diagnostic.Code && item.Line == diagnostic.Line &&
            item.Path is not null && SamePath(Path.GetFullPath(item.Path, input.Root), StagePath(input, diagnostic.Path)) &&
            item.ReportedProject is not null && SamePath(Path.GetFullPath(item.ReportedProject, input.Root), input.FullProjectPath) &&
            item.Message.Contains("'" + diagnostic.Identifier + "'", StringComparison.Ordinal));
    }
}
