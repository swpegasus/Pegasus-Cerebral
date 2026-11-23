using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

/// <summary>
/// Pure deterministic fixtures invoking the actual private installed-binding capture guard.
/// Open/admission/hello/dispatch counters are synthetic; no registry, service, pipe, Windows
/// authentication, installed transport, process restart or model inference is exercised.
/// </summary>
public static class InstalledWorkerClientCaptureTests
{
    private static readonly InstalledWorkerBinding Original = new(
        InstalledWorkerBinding.CurrentVersion, "123456781234123412341234567890ab",
        "S-1-5-21-100-200-300-1001", InstalledWorkerBinding.ExpectedServiceName,
        InstalledWorkerBinding.LocalServiceSid,
        @"C:\Program Files\Pegasus\Cerebral\Applications\Worker\Cerebral.Worker.exe",
        @"C:\ProgramData\Pegasus\Cerebral");

    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + label);
            checks++;
        }
        T Reject<T>(Action action, string label) where T : Exception
        {
            try { action(); }
            catch (T error) { checks++; return error; }
            throw new InvalidOperationException("FAIL: " + label);
        }

        Original.Validate();
        var captures = 0;
        var capture = Guard.Capture(() => { captures++; return Original; });
        Check(captures == 1, "Construction must read the initial enrollment exactly once.");
        Check(Guard.Type.IsNestedPrivate && Guard.Type.IsSealed &&
            Guard.Type.GetField("binding", BindingFlags.Instance | BindingFlags.NonPublic)?.IsInitOnly == true &&
            Guard.Type.GetField("failure", BindingFlags.Instance | BindingFlags.NonPublic)?.IsInitOnly == true,
            "The production capture must stay private and retain readonly enrollment/failure fields.");
        var rereads = 0;
        var equalCopy = Original with { };
        Check(!ReferenceEquals(equalCopy, Original) && equalCopy == Original,
            "An equal newly deserialized fixture record must be distinct from the captured object.");
        Check(ReferenceEquals(capture.RequireCurrent(() => { rereads++; return equalCopy; }), Original) &&
            captures == 1 && rereads == 1,
            "Successful comparison must return the original immutable enrollment object without recapture.");
        Check(ReferenceEquals(capture.RequireCurrent(() => { rereads++; return Original; }), Original) &&
            captures == 1 && rereads == 2,
            "Repeated comparisons must keep the same original capture.");

        var changes = new (string Field, InstalledWorkerBinding Value)[]
        {
            ("Version", Original with { Version = Original.Version + 1 }),
            ("BindingId", Original with { BindingId = "223456781234123412341234567890ab" }),
            ("OwnerSid", Original with { OwnerSid = "S-1-5-21-100-200-300-1002" }),
            ("ServiceName", Original with { ServiceName = "AnotherService" }),
            ("ServiceAccountSid", Original with { ServiceAccountSid = "S-1-5-18" }),
            ("WorkerExecutable", Original with { WorkerExecutable = @"C:\Program Files\Pegasus\Other\Cerebral.Worker.exe" }),
            ("DataDirectory", Original with { DataDirectory = @"C:\ProgramData\Pegasus\OtherCerebral" })
        };
        Check(changes.Length == 7 && typeof(InstalledWorkerBinding).GetProperties()
            .Count(property => property.DeclaringType == typeof(InstalledWorkerBinding) && property.GetMethod?.IsPublic == true) == 7,
            "The drift matrix must cover all seven public enrollment fields.");
        foreach (var (field, changed) in changes)
        {
            var reads = 0;
            var error = Reject<UnauthorizedAccessException>(() => capture.RequireCurrent(() => { reads++; return changed; }),
                field + " drift must be rejected by the actual complete-record comparison.");
            Check(reads == 1 && error.Message.Contains("changed after this client was created", StringComparison.Ordinal) &&
                error.Message.Contains("No command was sent", StringComparison.Ordinal),
                field + " drift must be diagnosed before operation admission.");
            if (field != "BindingId")
                Check(changed.BindingId == Original.BindingId,
                    field + " drift must be rejected even when the installed pipe target ID is unchanged.");
        }
        Reject<UnauthorizedAccessException>(() => capture.RequireCurrent(() => null),
            "Removed enrollment must not be accepted by a previously captured client.");
        Check(ReferenceEquals(capture.RequireCurrent(() => equalCopy), Original),
            "Rejected drift must not mutate the pinned enrollment; explicit restoration remains comparable.");

        VerifyUnavailableCapture(Check, Reject<UnauthorizedAccessException>);
        VerifyInvalidCapture(Check, Reject<UnauthorizedAccessException>);
        VerifyOperationBoundaries(Check, Reject<UnauthorizedAccessException>);
        VerifyPublicSurface(Check);
        var fatal = new OutOfMemoryException("Synthetic fatal capture failure.");
        var propagated = Reject<OutOfMemoryException>(() => Guard.Capture(() => throw fatal),
            "Fatal memory failure must escape Capture rather than become an offline enrollment.");
        Check(ReferenceEquals(propagated, fatal),
            "Reflection helpers must preserve the original fatal exception object.");

        Console.WriteLine($"Installed Worker client capture: {checks} deterministic private-guard assertions passed. Guard-before-fake-open/admission/dispatch checks are synthetic; no installed enrollment, registry, Windows pipe authentication, live service, real process restart or inference was exercised.");
        return checks;
    }

    private static void VerifyUnavailableCapture(Action<bool, string> check,
        Func<Action, string, UnauthorizedAccessException> reject)
    {
        var errors = new Exception?[]
        {
            null,
            new IOException("Synthetic enrollment read failed."),
            new UnauthorizedAccessException("Synthetic enrollment protection rejected."),
            new InvalidDataException("Synthetic enrollment document rejected."),
            new ArgumentException("Synthetic enrollment input rejected."),
            new OperationCanceledException("Synthetic enrollment read cancelled."),
            new InvalidOperationException("Synthetic enrollment reader unavailable.")
        };
        foreach (var failure in errors)
        {
            var initialReads = 0;
            // Capturing no enrollment or a recoverable reader error must preserve offline
            // construction. This helper never constructs an installed client or reads its registry.
            var failed = Guard.Capture(() =>
            {
                initialReads++;
                if (failure is not null) throw failure;
                return null;
            });
            check(initialReads == 1, "Unavailable capture must construct after exactly one initial read.");
            var laterReads = 0;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var operation = new SyntheticOperation();
                var error = reject(() => operation.Execute(failed, () => { laterReads++; return Original; }),
                    "Unavailable capture must fail before later enrollment read or fake operation admission.");
                check(laterReads == 0 && operation.Reads == 0 && operation.Opens == 0 &&
                    operation.Admissions == 0 && operation.Hellos == 0 && operation.Dispatches == 0,
                    "Failed capture must never recapture, rebind, open, admit, say hello or dispatch.");
                check(ReferenceEquals(error.InnerException, failure) &&
                    error.Message.Contains("could not be captured", StringComparison.Ordinal) &&
                    error.Message.Contains("new client", StringComparison.Ordinal),
                    "The unavailable-capture diagnostic must retain the original reader failure and require explicit replacement.");
            }
            var freshReads = 0;
            var fresh = Guard.Capture(() => { freshReads++; return Original; });
            check(freshReads == 1 && ReferenceEquals(fresh.RequireCurrent(() => Original), Original) && laterReads == 0,
                "Only an explicitly fresh capture may observe recovered valid enrollment.");
        }
    }

    private static void VerifyInvalidCapture(Action<bool, string> check,
        Func<Action, string, UnauthorizedAccessException> reject)
    {
        foreach (var invalid in new[]
        {
            Original with { Version = 999 },
            Original with { BindingId = Guid.Empty.ToString("N") },
            Original with { DataDirectory = @"\\foreign\share\Cerebral" }
        })
        {
            var initialReads = 0;
            var captured = Guard.Capture(() => { initialReads++; return invalid; });
            check(initialReads == 1, "Invalid initial enrollment must defer failure without abandoning construction.");
            var laterReads = 0;
            var operation = new SyntheticOperation();
            var error = reject(() => operation.Execute(captured, () => { laterReads++; return Original; }),
                "Initial validation failure must block the operation before consulting recovered enrollment.");
            check(error.InnerException is InvalidDataException && laterReads == 0 &&
                operation.Opens == 0 && operation.Admissions == 0 && operation.Dispatches == 0,
                "An invalid initial record must retain validation failure without accepting a later valid replacement.");
        }
    }

    private static void VerifyOperationBoundaries(Action<bool, string> check,
        Func<Action, string, UnauthorizedAccessException> reject)
    {
        var next = Original with
        {
            BindingId = "323456781234123412341234567890ab",
            DataDirectory = @"C:\ProgramData\Pegasus\ReplacementCerebral"
        };
        next.Validate();
        var captureReads = 0;
        var original = Guard.Capture(() => { captureReads++; return Original; });
        var preOpen = new SyntheticOperation();
        reject(() => preOpen.Execute(original, () => next),
            "Pre-open enrollment drift must stop the synthetic operation.");
        check(preOpen.Reads == 1 && preOpen.Opens == 0 && preOpen.Admissions == 0 &&
            preOpen.Hellos == 0 && preOpen.Dispatches == 0 && preOpen.OpenedBindings.Count == 0 && captureReads == 1,
            "Actual pre-open guard rejection must prevent fake opening and any new-target substitution.");

        var afterHello = new SyntheticOperation();
        var reads = 0;
        reject(() => afterHello.Execute(original, () => ++reads == 1 ? Original : next),
            "Enrollment drift after synthetic hello must stop before fake command dispatch.");
        check(reads == 2 && afterHello.Reads == 2 && afterHello.Opens == 1 &&
            afterHello.Admissions == 1 && afterHello.Hellos == 1 && afterHello.Dispatches == 0 &&
            afterHello.OpenedBindings.Count == 1 && ReferenceEquals(afterHello.OpenedBindings[0], Original),
            "Post-hello guard rejection must retain the original fake target and dispatch nothing to its replacement.");
        check(ReferenceEquals(original.RequireCurrent(() => Original), Original) && captureReads == 1,
            "Failed operation comparisons must never change or recapture the original binding.");

        // Process generations are deliberately synthetic facts outside the enrollment record.
        // This proves only that a value-equal enrollment survives the guard, not real restart admission.
        var restarted = new SyntheticOperation();
        restarted.Execute(original, () => Original with { }, processGeneration: 1);
        restarted.Execute(original, () => Original with { }, processGeneration: 2);
        check(restarted.Reads == 4 && restarted.Opens == 2 && restarted.Admissions == 2 &&
            restarted.Hellos == 2 && restarted.Dispatches == 2 &&
            restarted.ProcessGenerations.SequenceEqual(new[] { 1, 2 }) &&
            restarted.OpenedBindings.All(value => ReferenceEquals(value, Original)),
            "Unchanged enrollment across synthetic process generations must remain admitted using the original capture.");

        var freshReads = 0;
        var fresh = Guard.Capture(() => { freshReads++; return next; });
        var replacement = new SyntheticOperation();
        replacement.Execute(fresh, () => next with { });
        check(freshReads == 1 && replacement.Dispatches == 1 &&
            ReferenceEquals(fresh.RequireCurrent(() => next), next) &&
            replacement.OpenedBindings.Count == 1 && ReferenceEquals(replacement.OpenedBindings[0], next),
            "An explicitly fresh private capture must see and return the new valid record without substituting it into the old capture.");
        reject(() => original.RequireCurrent(() => next),
            "Fresh capture success must not make the old capture accept replacement enrollment.");
    }

    private static void VerifyPublicSurface(Action<bool, string> check)
    {
        foreach (var type in new[] { typeof(LocalWorkerClient), typeof(CerebralClient) })
        {
            var factories = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == "ForInstalledWorker").ToArray();
            check(factories.Length == 1 && factories[0].GetParameters().Length == 0 &&
                factories[0].ReturnType == type && !factories[0].IsGenericMethod,
                type.Name + " must expose only its parameterless installed factory.");
            var publicMembers = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
            check(publicMembers.All(member => member.GetParameters().All(parameter =>
                !InjectionType(parameter.ParameterType))),
                type.Name + " must not expose binding-reader, arbitrary installed binding or injected local-client parameters.");
        }
        var localConstructors = typeof(LocalWorkerClient).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        check(localConstructors.Length == 1 && localConstructors[0].GetParameters() is [{ ParameterType: var endpoint }] &&
            endpoint == typeof(LocalWorkerEndpoint),
            "The established development endpoint constructor must remain separate; no public installed/noninstalled toggle may be added.");
    }

    private static bool InjectionType(Type type)
    {
        if (type == typeof(InstalledWorkerBinding) || type == typeof(LocalWorkerClient) ||
            type == Guard.Type || typeof(Delegate).IsAssignableFrom(type)) return true;
        return type.HasElementType && InjectionType(type.GetElementType()!) ||
            type.IsGenericType && type.GetGenericArguments().Any(InjectionType);
    }

    /// <summary>Synthetic operation stages around the actual private capture guard, never installed transport.</summary>
    private sealed class SyntheticOperation
    {
        public int Reads { get; private set; }
        public int Opens { get; private set; }
        public int Admissions { get; private set; }
        public int Hellos { get; private set; }
        public int Dispatches { get; private set; }
        public List<InstalledWorkerBinding> OpenedBindings { get; } = [];
        public List<int> ProcessGenerations { get; } = [];

        public void Execute(Guard capture, Func<InstalledWorkerBinding?> read, int processGeneration = 1)
        {
            InstalledWorkerBinding? Current() { Reads++; return read(); }
            // The production ConnectAsync calls these same two guard methods before opening
            // and after validated hello. Everything between/beyond them here is a counter fixture.
            var original = capture.RequireCurrent(Current);
            Opens++;
            OpenedBindings.Add(original);
            ProcessGenerations.Add(processGeneration);
            Admissions++;
            Hellos++;
            var admitted = capture.RequireCurrent(Current);
            if (!ReferenceEquals(admitted, original))
                throw new InvalidOperationException("The production guard substituted a replacement binding.");
            Dispatches++;
        }
    }

    private sealed class Guard
    {
        public static Type Type { get; } = typeof(LocalWorkerClient).GetNestedType("InstalledBindingCapture", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing production private InstalledBindingCapture.");
        private static readonly MethodInfo CaptureMethod = Type.GetMethod("Capture", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing production capture method.");
        private static readonly MethodInfo RequireMethod = Type.GetMethod("RequireCurrent", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Missing production RequireCurrent method.");
        private readonly object capture;
        private Guard(object capture) => this.capture = capture;
        public static Guard Capture(Func<InstalledWorkerBinding?> read) => new(Invoke(CaptureMethod, null, read));
        public InstalledWorkerBinding RequireCurrent(Func<InstalledWorkerBinding?> read) =>
            (InstalledWorkerBinding)Invoke(RequireMethod, capture, read);

        private static object Invoke(MethodInfo method, object? instance, Func<InstalledWorkerBinding?> read)
        {
            try { return method.Invoke(instance, [read]) ?? throw new InvalidOperationException("The production capture guard returned null."); }
            catch (TargetInvocationException error) when (error.InnerException is { } inner)
            {
                ExceptionDispatchInfo.Capture(inner).Throw();
                throw;
            }
        }
    }
}
