using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Core;

namespace Cerebral.Worker;

public sealed partial class WorkerWorkflowEffects
{
    // Extend the exact legacy ToolCommand output schema in place. The ordinary shape and
    // required fields remain unchanged; only the two optional host-owned fields are admitted.
    private static JsonElement AdmittedToolOutputSchema(JsonElement legacy)
    {
        var schema = JsonNode.Parse(legacy.GetRawText())!.AsObject();
        var properties = schema["properties"]!.AsObject();
        properties["verification"] = JsonNode.Parse("""
        {"type":"object","properties":{
          "schemaVersion":{"enum":[1]},"recipeSha256":{"type":"string","minLength":64,"maxLength":64},
          "inputSha256":{"type":"string","minLength":64,"maxLength":64},
          "effectId":{"type":"string","minLength":1,"maxLength":128},
          "processDispatched":{"type":"boolean"},"complete":{"type":"boolean"},
          "exitCode":{"oneOf":[{"type":"integer"},{"type":"null"}]},
          "reportSha256":{"oneOf":[{"type":"string","maxLength":64},{"type":"null"}]},
          "inputs":{"type":"array","maxItems":512,"items":{"type":"object","properties":{
            "role":{"type":"string","maxLength":64},"path":{"type":"string","maxLength":4096},
            "sha256":{"type":"string","minLength":64,"maxLength":64},"bytes":{"type":"integer","minimum":0,"maximum":67108864}},
            "required":["role","path","sha256","bytes"],"additionalProperties":false}}
        },"required":["schemaVersion","recipeSha256","inputSha256","effectId","processDispatched","complete","exitCode","inputs"],
        "additionalProperties":false}
        """);
        properties["verificationReport"] = OwnerVerificationReportSchemaNode();
        properties["ownerAction"] = JsonNode.Parse("""
        {"type":"object","properties":{
          "schemaVersion":{"enum":[1]},"kind":{"enum":["working-directory-required"]},
          "sourceRunId":{"type":"string","minLength":1,"maxLength":128},
          "sourceRequestId":{"type":"string","minLength":1,"maxLength":128},
          "effectId":{"type":"string","minLength":1,"maxLength":128},
          "toolName":{"type":"string","minLength":1,"maxLength":256},
          "argumentsSha256":{"type":"string","minLength":64,"maxLength":64},
          "propertiesRevision":{"type":"integer","minimum":0},"noEffect":{"enum":[true]}
        },"required":["schemaVersion","kind","sourceRunId","sourceRequestId","effectId",
          "toolName","argumentsSha256","propertiesRevision","noEffect"],"additionalProperties":false}
        """);
        properties["internetAction"] = JsonNode.Parse("""
        {"type":"object","properties":{
          "schemaVersion":{"enum":[1]},"kind":{"enum":["internet-owner-approval"]},
          "approvalRequestId":{"type":"string","minLength":64,"maxLength":64},
          "requestDigest":{"type":"string","minLength":64,"maxLength":64},
          "sourceRunId":{"type":"string","minLength":1,"maxLength":128},
          "effectId":{"type":"string","minLength":1,"maxLength":128},
          "toolName":{"type":"string","minLength":1,"maxLength":256},
          "argumentsSha256":{"type":"string","minLength":64,"maxLength":64},"noEffect":{"enum":[true]}
        },"required":["schemaVersion","kind","approvalRequestId","requestDigest","sourceRunId","effectId","toolName","argumentsSha256","noEffect"],"additionalProperties":false}
        """);
        return CerebralJson.Element(schema);
    }

    // The checker report is inspected before constructing ToolData, so reject extra or
    // malformed fields at the same closed boundary used by the durable tool output.
    private static JsonNode OwnerVerificationReportSchemaNode() => JsonNode.Parse("""
        {"type":"object","properties":{
          "schema":{"type":"string","minLength":1,"maxLength":256},"checkerVersion":{"type":"string","minLength":1,"maxLength":64},
          "tier":{"type":"string","maxLength":256},"descriptorSha256":{"type":"string","minLength":64,"maxLength":64},
          "checkerClosureSha256":{"type":"string","minLength":64,"maxLength":64},
          "targetClosureSha256":{"type":"string","minLength":64,"maxLength":64},
          "stagedPins":{"type":"array","minItems":1,"maxItems":512,"items":{"type":"object","properties":{
            "Role":{"type":"string","maxLength":64},"Path":{"type":"string","maxLength":512},
            "Sha256":{"type":"string","minLength":64,"maxLength":64},"Bytes":{"type":"integer","minimum":0,"maximum":67108864}},
            "required":["Role","Path","Sha256","Bytes"],"additionalProperties":false}},
          "checkerImage":{"type":"object","properties":{
            "Role":{"type":"string","minLength":1,"maxLength":64},"Sha256":{"type":"string","minLength":64,"maxLength":64},
            "Bytes":{"type":"integer","minimum":1,"maximum":67108864},
            "AssemblyIdentity":{"type":"string","maxLength":1024},"ModuleVersionId":{"type":"string","maxLength":128}},
            "required":["Role","Sha256","Bytes","AssemblyIdentity","ModuleVersionId"],"additionalProperties":false},
          "actualLoadedBytes":{"type":"array","minItems":1,"maxItems":512,"items":{"type":"object","properties":{
            "Role":{"type":"string","maxLength":64},"Sha256":{"type":"string","minLength":64,"maxLength":64},
            "Bytes":{"type":"integer","minimum":1,"maximum":67108864},
            "AssemblyIdentity":{"type":"string","maxLength":1024},"ModuleVersionId":{"type":"string","maxLength":128}},
            "required":["Role","Sha256","Bytes","AssemblyIdentity","ModuleVersionId"],"additionalProperties":false}},
          "stagedClosureUnchanged":{"enum":[true]},
          "checks":{"type":"array","minItems":1,"items":{"type":"object","properties":{
            "Name":{"type":"string","maxLength":128},"Passed":{"type":"boolean"},
            "expected":{"type":"string","maxLength":128},"actual":{"type":"string","maxLength":512},
            "exception":{"oneOf":[{"type":"string","maxLength":128},{"type":"null"}]},
            "actualTruncated":{"type":"boolean"},"exceptionTruncated":{"oneOf":[{"type":"boolean"},{"type":"null"}]}},
            "required":["Name","Passed","expected","actual","exception","actualTruncated","exceptionTruncated"],
            "additionalProperties":false}},
          "totalCount":{"type":"integer","minimum":1},"passed":{"type":"boolean"},
          "passedCount":{"type":"integer","minimum":0},
          "failedCount":{"type":"integer","minimum":0},
          "failureDiagnostic":{"type":"string","maxLength":480},
          "installed":{"enum":[false]},"mainRendered":{"enum":[false]},"windowShown":{"enum":[false]},
          "observedUtc":{"type":"string","maxLength":64},"scope":{"type":"string","maxLength":256}
        },"required":["schema","checkerVersion","tier","descriptorSha256","checkerClosureSha256",
          "targetClosureSha256","stagedPins","checkerImage","actualLoadedBytes","stagedClosureUnchanged",
          "checks","totalCount","passed","passedCount","failedCount","failureDiagnostic",
          "installed","mainRendered","windowShown","observedUtc","scope"],"additionalProperties":false}
        """)!;
}
