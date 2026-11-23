namespace Cerebral.Core;

/// <summary>Owner editor projection without model, grant or plugin settings.</summary>
public sealed record AgentInstructionDocument(string ProfileId, string Content, int Revision);
public sealed record AgentInstructionSaveRequest(string ProfileId, string Content, int ExpectedRevision);
