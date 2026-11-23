using System.IO;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

// Pure admission and client-protocol checks. No endpoint, Main window, Worker, or Exit effect is used.
internal static class MainCliExitChecks
{
    private sealed class Clock : TimeProvider
    {
        internal long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }

    internal static async Task<int> VerifyAsync()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        var instance = Guid.NewGuid().ToString("N");
        var clock = new Clock();
        var admission = new MainExitAdmission(clock);
        var exits = 0;
        DesktopControlResponse Handle(MainExitAdmission gate, string operation, object arguments, bool ready = true) =>
            gate.Handle(new DesktopControlRequest(instance, Guid.NewGuid().ToString("N"), operation,
                CerebralJson.Element(arguments)), instance, ready, () => exits++);

        var first = Handle(admission, DesktopControlProtocol.FullExitPrepare, new { });
        var token = first.Data.GetProperty("admissionToken").GetString()!;
        Check(first.State == "completed" && exits == 0 && token.Length == 64,
            "Preparation must return a token without an Exit effect.");
        Check(Handle(admission, DesktopControlProtocol.FullExitPrepare, new { }).Data
            .GetProperty("admissionToken").GetString() == token,
            "Repeated preparation must retain the current admission.");
        Check(Handle(admission, DesktopControlProtocol.FullExitCommit,
            new { instanceId = instance, admissionToken = new string('0', 64) }).State == "rejected" && exits == 0,
            "A guessed token must not dispatch Exit.");
        Check(Handle(admission, DesktopControlProtocol.FullExitCommit,
            new { instanceId = Guid.NewGuid().ToString("N"), admissionToken = token }).State == "rejected" && exits == 0,
            "A different Main generation must not commit.");
        clock.Seconds = 30;
        Check(Handle(admission, DesktopControlProtocol.FullExitCommit,
            new { instanceId = instance, admissionToken = token }).State == "rejected" && exits == 0,
            "Expired admission must not dispatch Exit.");
        var renewed = Handle(admission, DesktopControlProtocol.FullExitPrepare, new { }).Data
            .GetProperty("admissionToken").GetString()!;
        Check(renewed != token, "Expired preparation must rotate its token.");
        var committed = Handle(admission, DesktopControlProtocol.FullExitCommit,
            new { instanceId = instance, admissionToken = renewed });
        Check(committed.State == "accepted" && exits == 1 &&
            committed.Data.GetProperty("admissionAcknowledged").GetBoolean(),
            "Only the current token may dispatch one Exit request.");
        Check(Handle(admission, DesktopControlProtocol.FullExitCommit,
            new { instanceId = instance, admissionToken = renewed }).State == "rejected" && exits == 1,
            "A consumed token must not dispatch again.");
        Check(Handle(new MainExitAdmission(new Clock()), DesktopControlProtocol.FullExitPrepare,
            new { }, ready: false).State == "rejected" && exits == 1,
            "Main without its ordinary tray handler must refuse Exit admission.");

        var normal = DesktopControlServer.ModeCapabilities(false, false, true);
        var admitted = normal.GetProperty("admittedOperations").EnumerateArray()
            .Select(item => item.GetString()).ToArray();
        Check(admitted.Contains(DesktopControlProtocol.FullExitPrepare) &&
            admitted.Contains(DesktopControlProtocol.FullExitCommit),
            "Normal Main must advertise both Exit operations.");
        foreach (var mode in new[] { (false, false, false), (true, false, false), (false, true, false) })
        {
            var other = DesktopControlServer.ModeCapabilities(mode.Item1, mode.Item2, mode.Item3);
            Check(!other.GetProperty("admittedOperations").EnumerateArray().Any(item =>
                item.GetString() is DesktopControlProtocol.FullExitPrepare or DesktopControlProtocol.FullExitCommit),
                "Other control modes must not advertise Exit.");
        }

        DesktopControlResponse Capabilities(string requestId) =>
            new(requestId, DesktopControlProtocol.CapabilitiesInspect, "completed",
                CerebralJson.Element(new { endpoint = new { instanceId = instance }, desktopControl = normal }));
        var calls = new List<string>();
        var prepared = false;
        var client = new MainExitClientSession(instance);
        var target = new MainExitAdmission(new Clock());
        var result = await client.ExecuteAsync((operation, requestId, json) =>
        {
            calls.Add(operation);
            if (operation == DesktopControlProtocol.CapabilitiesInspect)
                return Task.FromResult(Capabilities(requestId));
            using var document = JsonDocument.Parse(json);
            if (operation == DesktopControlProtocol.FullExitCommit)
                Check(prepared && exits == 1, "Client commit must follow a read admission.");
            var response = target.Handle(new DesktopControlRequest(instance, requestId, operation,
                document.RootElement.Clone()), instance, true, () => exits++);
            if (operation == DesktopControlProtocol.FullExitPrepare) prepared = true;
            return Task.FromResult(response);
        });
        Check(result.State == "accepted" && exits == 2 && client.CommitAttempted &&
            calls.SequenceEqual([DesktopControlProtocol.CapabilitiesInspect,
                DesktopControlProtocol.FullExitPrepare, DesktopControlProtocol.FullExitCommit]),
            "Client and owner must complete one ordered handshake.");

        calls.Clear();
        var unknown = new MainExitClientSession(instance);
        var lostReply = false;
        try
        {
            await unknown.ExecuteAsync((operation, requestId, json) =>
            {
                calls.Add(operation);
                if (operation == DesktopControlProtocol.CapabilitiesInspect)
                    return Task.FromResult(Capabilities(requestId));
                if (operation == DesktopControlProtocol.FullExitPrepare)
                    return Task.FromResult(new DesktopControlResponse(requestId, operation, "completed",
                        CerebralJson.Element(new
                        {
                            schema = "cerebral.main-exit-admission/1", instanceId = instance,
                            admissionToken = new string('A', 64), effect = "none",
                            next = DesktopControlProtocol.FullExitCommit
                        })));
                throw new IOException("Deterministic lost commit reply; no IPC.");
            });
        }
        catch (IOException) { lostReply = true; }
        Check(lostReply && unknown.CommitAttempted &&
            calls.SequenceEqual([DesktopControlProtocol.CapabilitiesInspect,
                DesktopControlProtocol.FullExitPrepare, DesktopControlProtocol.FullExitCommit]),
            "A lost commit reply must remain an attempted, unretried dispatch.");

        var priorError = Console.Error;
        using var errorText = new StringWriter();
        try
        {
            Console.SetError(errorText);
            Check(Program.Main(["--exit", "invalid"]) == 2 &&
                errorText.ToString().Contains("Usage:", StringComparison.Ordinal) &&
                System.Windows.Application.Current is null,
                "Invalid CLI arguments must fail without constructing WPF Main.");
        }
        finally { Console.SetError(priorError); }
        return checks;
    }
}
