namespace Cerebral.Programming;

/// <summary>Plugin-owned development/review policy shared by the existing model entry points.</summary>
internal static class ProgrammingDevelopmentGuidance
{
    internal const string FailedBehaviorDiagnosis = """
        A successful build proves compilation, not the requested behavior. For a real failed behavior
        check, compare its expected/actual result and use the host-advertised canonical original source
        and receipt coordinates; retrieve only missing detail. Trace the failing input through the
        complete handler, including later assignments and exits that can overwrite an earlier result.
        Identify a correction to that causal path while preserving established behavior and recovery.
        Changed hashes, counters or renamed variables alone do not prove a meaningful correction.
        Compiled DLL/EXE files are binary artifacts, not text source. Do not use file.read or file.inspect
        as a binary reader. Use canonical original source and manifest metadata; use a format-specific
        tool only when it is advertised and permitted. For behavior, use the actual admitted host checker
        and its complete current typed receipt. After a complete current typed checker pass, retain it
        and continue only the remaining verify/present/final steps required by the accepted task.
        Reuse current verification and an existing pending presentation when applicable; do not create
        a second presentation or rerun unchanged successful checks for validation-only followthrough.
        A binary read is not a follow-up verification step after a current typed checker pass.
        Inspect the project/output type and intended host before choosing validation. A host plugin or
        class library is not a standalone app: build it, then use its admitted host checker and
        plugin.verify/plugin.present when requested. A dotnet run error reporting OutputType Library or
        no executable entry point identifies a wrong validation route. Changing run flags does not fix
        that mismatch; a successful build between attempts does not repair the failed run.
        Do not add Main or force OutputType=Exe merely to make a host plugin runnable. If configured
        checks require an incompatible run, report the mismatch for owner correction rather than
        bypassing the accepted checks. When an admitted behavior check is unavailable, report that gap.
        After a material correction, use the configured checks on the current built artifacts; earlier
        results cannot verify changed inputs. Match each claimed output to the user's purpose and its
        applicable current receipt. Report failed, stale, unavailable or untested behavior truthfully.
        This guidance adds no tools, grants or authority.
        """;
}
