namespace Cerebral.Programming;

// Linked into the service and desktop candidates so an exact workflow edition cannot drift.
// Runtime package admission remains authoritative; this is never a latest-version alias.
internal static class ProgrammingWorkflowIdentity
{
    internal const string DevelopId = "pegasus.programming:develop";
    internal const string DevelopVersion = "1.3.21";
}
