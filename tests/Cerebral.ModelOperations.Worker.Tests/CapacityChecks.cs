using Cerebral.Core;
using Cerebral.Worker.ModelOperations;

public static class CapacityChecks
{
    private const long GiB = 1024L * 1024 * 1024;
    public static Task<int> RunAsync()
    {
        var passed = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }
        void Refuse(Action action, string code)
        { try { action(); } catch (ModelOperationAdmissionException error) when (error.Code == code) { passed++; return; } throw new Exception("Expected admission refusal " + code); }
        static ResourceInfo Unified(long availableGiB) => new(0, availableGiB * 1024,
            [new("GB10 fixture", 128 * 1024, 1, true)]) { SystemTotalMb = 128 * 1024 };
        var ledger = new ModelOperationCapacityLedger();
        var resident = ledger.Reserve(new("model", "instance", 24 * GiB, 16 * GiB, true), Unified(64), 8 * 1024);
        Check(ledger.StillPromisedMb == 16 * 1024, "Pending model allocation is reserved before provider execution.");
        Refuse(() => ledger.Reserve(new("other", "other-instance", 24 * GiB, 48 * GiB, true), Unified(64), 8 * 1024), "capacity_busy");
        ledger.Materialize(resident.Id);
        Check(ledger.StillPromisedMb == 0 && ledger.Snapshot().Count == 1, "Observed resident remains owned without a second deduction from current free memory.");
        var peak = ledger.Reserve(new("model", "instance", 24 * GiB, 32 * GiB, true), Unified(48), 8 * 1024);
        Check(ledger.StillPromisedMb == 32 * 1024, "Active inference/export demand is separate from resident allocation.");
        ledger.Unknown(peak.Id);
        Check(ledger.Snapshot().Single(x => x.Id == peak.Id).Unknown && ledger.StillPromisedMb == 32 * 1024,
            "An uncertain provider/export retains its reservation.");
        Refuse(() => ledger.Reserve(new("other", "other-instance", 24 * GiB, 16 * GiB, true), Unified(48), 8 * 1024), "capacity_busy");
        ledger.ReleaseConfirmed(peak.Id); ledger.ReleaseConfirmed(resident.Id);
        Check(ledger.Snapshot().Count == 0, "Only explicit confirmed retirement frees ownership.");
        var discrete16 = new ResourceInfo(0, 128 * 1024, [new("16 GiB fixture", 16 * 1024, 16 * 1024, false)]) { SystemTotalMb = 128 * 1024 };
        Refuse(() => ledger.Reserve(new("model", "instance", 24 * GiB, 8 * GiB, false), discrete16, 1024), "device_memory");
        var separatelyQualified = ledger.Reserve(new("future-profile-model", "future-instance", 12 * GiB, 8 * GiB, false), discrete16, 1024);
        Check(separatelyQualified.StillPromisedMb == 8192, "Device rejection belongs to a selected profile, not a permanent device-class ban.");
        ledger.ReleaseConfirmed(separatelyQualified.Id);
        Refuse(() => ledger.Reserve(new("model", "instance", 24 * GiB, 8 * GiB, true), Unified(64) with { SystemAvailableMb = null }, 1024), "capacity_unknown");
        Refuse(() => ledger.Reserve(new("model", "instance", 24 * GiB, 8 * GiB, false), Unified(64), 1024), "device_profile");

        // Two contenders receive the same deliberately stale observation. Atomic pending
        // reservation accounting still prevents both from spending the free pool.
        var shared = new ModelOperationCapacityLedger(); var successes = 0; var failures = 0;
        Parallel.For(0, 2, i =>
        {
            try { shared.Reserve(new("model-" + i, "instance-" + i, 24 * GiB, 40 * GiB, true), Unified(64), 8 * 1024); Interlocked.Increment(ref successes); }
            catch (ModelOperationAdmissionException e) when (e.Code == "capacity_busy") { Interlocked.Increment(ref failures); }
        });
        Check(successes == 1 && failures == 1 && shared.StillPromisedMb == 40 * 1024, "Concurrent stale-snapshot contenders cannot double-spend capacity.");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { state = "passed", capacityChecks = passed,
            evidence = "real host ledger and deterministic resource observations; no hardware allocation" }));
        return Task.FromResult(passed);
    }
}
