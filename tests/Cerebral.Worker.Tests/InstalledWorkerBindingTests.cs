using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Client;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

/// <summary>Synthetic document/OS-fact policy fixtures. No registry, service, ACL, identity or pipe is modified or authenticated.</summary>
public static class InstalledWorkerBindingTests
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException("FAIL: " + label); checks++; }
        void Reject<T>(Action action, string label) where T : Exception
        {
            try { action(); }
            catch (T) { checks++; return; }
            throw new InvalidOperationException("FAIL: " + label);
        }
        var owner = "S-1-5-21-100-200-300-1001";
        var binding = new InstalledWorkerBinding(1, "123456781234123412341234567890ab", owner,
            InstalledWorkerBinding.ExpectedServiceName, InstalledWorkerBinding.LocalServiceSid,
            @"C:\Program Files\Pegasus\Cerebral\Applications\Worker\Cerebral.Worker.exe", @"C:\ProgramData\Pegasus\Cerebral");
        byte[] Encode(InstalledWorkerBinding value) => JsonSerializer.SerializeToUtf8Bytes(value, CerebralJson.Options);
        var bytes = Encode(binding);
        Check(InstalledWorkerBinding.Parse(bytes) == binding, "strict enrollment document round trips");
        Check(InstalledWorkerBinding.Parse(Encoding.UTF8.GetBytes(binding.ToJson())) == binding,
            "canonical enrollment serialization revalidates and round trips");
        Check(binding.PipeEndpoint() == new LocalWorkerEndpoint("Pegasus.Cerebral.Service." + binding.BindingId, binding.BindingId),
            "installed pipe identity is derived only from the protected nonsecret binding ID");
        Check(InstalledWorkerBindingStore.RegistryPath == @"Software\Pegasus\Cerebral\LocalTransport"
            && InstalledWorkerBindingStore.BindingValueName == "BindingJson", "binding reader has a fixed machine registry location");
        void RejectDocument(Action<JsonObject> change)
        {
            var value = JsonNode.Parse(bytes)!.AsObject(); change(value);
            Reject<InvalidDataException>(() => InstalledWorkerBinding.Parse(Encoding.UTF8.GetBytes(value.ToJsonString())), "invalid enrollment document accepted");
        }
        RejectDocument(value => value.Remove("ownerSid"));
        RejectDocument(value => value["ownerSid"] = null);
        RejectDocument(value => value["OwnerSid"] = owner);
        RejectDocument(value => value["credential"] = "DO-NOT-EXPORT");
        RejectDocument(value => value["version"] = 2);
        RejectDocument(value => value["bindingId"] = Guid.Empty.ToString("N"));
        var duplicate = Encoding.UTF8.GetString(bytes).TrimEnd();
        duplicate = duplicate[..^1] + ",\"ownerSid\":\"" + owner + "\"}";
        Reject<InvalidDataException>(() => InstalledWorkerBinding.Parse(Encoding.UTF8.GetBytes(duplicate)), "duplicate owner field accepted");
        Reject<InvalidDataException>(() => InstalledWorkerBinding.Parse(new byte[InstalledWorkerBinding.MaximumDocumentBytes + 1]), "oversized enrollment accepted");
        Reject<InvalidDataException>(() => InstalledWorkerBinding.Parse([0x7B, 0x22, 0xFF, 0x22, 0x7D]), "malformed UTF-8 accepted");
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-19", "S-1-5-32-544", "S-1-5-21-100-200-300", "S-1-5-21-100-200-300-01001", "current-user" })
            Reject<InvalidDataException>(() => (binding with { OwnerSid = sid }).Validate(), "implicit, service or noncanonical owner accepted");
        (binding with { OwnerSid = "S-1-12-1-100-200-300-400" }).Validate(); checks++;
        Reject<InvalidDataException>(() => (binding with { ServiceAccountSid = InstalledWorkerAdmission.SystemSid }).Validate(), "implicit LocalSystem elevation accepted");
        Reject<InvalidDataException>(() => (binding with { ServiceName = "OtherService" }).Validate(), "other service enrollment accepted");
        foreach (var path in new[] { @"\\server\share\state", @"\\?\C:\State", @"\\.\C:\State", @"C:\State:secret", @"C:\a\..\State", @"C:\State.", @"C:\", @"C:\NUL", @"C:\COM1.txt", "C:\\State\nOther" })
            Reject<InvalidDataException>(() => (binding with { DataDirectory = path }).Validate(), "unsafe data path accepted");
        Reject<InvalidDataException>(() => (binding with { DataDirectory = @"C:\Program Files\Pegasus\Cerebral\State" }).Validate(), "mutable data in program directory accepted");
        Reject<InvalidDataException>(() => (binding with { WorkerExecutable = @"C:\Tools\Other.exe" }).Validate(), "non-Worker executable accepted");

        var protectedKey = new InstalledWorkerRegistryProtection(InstalledWorkerAdmission.AdministratorsSid, true, true,
        [new(InstalledWorkerAdmission.AdministratorsSid, 0x000F003F, true), new(InstalledWorkerAdmission.SystemSid, 0x000F003F, true),
            new(InstalledWorkerBinding.LocalServiceSid, 0x00020019, true), new(owner, 0x00020019, true)]);
        InstalledWorkerAdmission.RequireProtectedRegistry(protectedKey); checks++;
        foreach (var protection in new[] { protectedKey with { OwnerSid = owner }, protectedKey with { DaclPresent = false }, protectedKey with { DaclProtected = false } })
            Reject<UnauthorizedAccessException>(() => InstalledWorkerAdmission.RequireProtectedRegistry(protection), "unprotected enrollment registry accepted");
        foreach (var sid in new[] { owner, InstalledWorkerBinding.LocalServiceSid, "S-1-1-0" })
            foreach (var rights in new uint[] { 2, 4, 32, 0x10000, 0x40000, 0x80000, 0x40000000, 0x10000000, 0x02000000 })
                Reject<UnauthorizedAccessException>(() => InstalledWorkerAdmission.RequireProtectedRegistry(protectedKey with
                { Entries = [.. protectedKey.Entries, new(sid, rights, true)] }), "unprivileged enrollment mutation right accepted");
        InstalledWorkerAdmission.RequireProtectedRegistry(protectedKey with { Entries = [.. protectedKey.Entries, new(owner, 2, false)] }); checks++;
        Reject<UnauthorizedAccessException>(() => InstalledWorkerAdmission.RequireProtectedRegistry(protectedKey with
        { Entries = Enumerable.Repeat(new InstalledWorkerRegistryAce(owner, 0x20019, true), 129).ToArray() }), "unbounded ACL accepted");

        var service = new InstalledWorkerServiceIdentity(binding.ServiceName, binding.ServiceAccountSid, binding.WorkerExecutable,
            binding.DataDirectory, 1234, InstalledWorkerServiceState.Running);
        var process = new InstalledWorkerProcessIdentity(1234, binding.ServiceAccountSid, binding.WorkerExecutable, 0);
        InstalledWorkerAdmission.RequireServiceHost(binding, service, process); checks++;
        Reject<UnauthorizedAccessException>(() => InstalledWorkerAdmission.RequireServiceHost(binding, service with { State = InstalledWorkerServiceState.StartPending }, process), "StartPending PID is not a verified running service identity");
        InstalledWorkerAdmission.RequirePipeServer(binding, service, process, 1234); checks++;
        var endpoint = new InstalledWorkerPipeEndpoint(1234, 0,
            new(binding.ServiceAccountSid, true, true,
            [new(binding.ServiceAccountSid, 0x001f01ff), new(owner, 0x0012019b)]));
        InstalledWorkerAdmission.RequirePipeServer(binding, service, endpoint); checks++;
        foreach (var changed in new[] { service with { State = InstalledWorkerServiceState.Stopped }, service with { State = InstalledWorkerServiceState.StopPending },
            service with { ProcessId = 4321 }, service with { ServiceName = "OtherService" }, service with { AccountSid = InstalledWorkerAdmission.SystemSid },
            service with { Executable = @"C:\Other\Cerebral.Worker.exe" }, service with { DataDirectory = @"C:\Other\State" } })
            Reject<UnauthorizedAccessException>(() => InstalledWorkerAdmission.RequireServiceHost(binding, changed, process), "wrong observed service accepted");
        foreach (var changed in new[] { process with { ProcessId = 4321 }, process with { AccountSid = owner }, process with { SessionId = 1 },
            process with { Executable = @"C:\Other\Cerebral.Worker.exe" } })
            Reject<UnauthorizedAccessException>(() => InstalledWorkerAdmission.RequireServiceHost(binding, service, changed), "wrong observed process accepted");
        Reject<UnauthorizedAccessException>(() => InstalledWorkerAdmission.RequirePipeServer(binding, service, process, 4321), "spoofed pipe PID accepted");
        Reject<UnauthorizedAccessException>(() => InstalledWorkerAdmission.RequirePipeServer(binding, service with { State = InstalledWorkerServiceState.StartPending }, process, 1234), "client admitted before service running");
        Reject<UnauthorizedAccessException>(() => InstalledWorkerAdmission.RequirePipeServer(binding, service, endpoint with { ProcessId = 4321 }), "SCM/pipe PID mismatch accepted");
        Reject<UnauthorizedAccessException>(() => InstalledWorkerAdmission.RequirePipeServer(binding, service, endpoint with { SessionId = 1 }), "interactive-session service pipe accepted");
        var client = new InstalledWorkerClientIdentity(owner, 1, true, false, false);
        InstalledWorkerAdmission.RequireOwnerClient(binding, client); checks++;
        InstalledWorkerAdmission.RequireOwnerClient(binding, client with { SessionId = 7 }); checks++;
        foreach (var changed in new[] { client with { UserSid = "S-1-5-21-100-200-300-1002" }, client with { SessionId = 0 },
            client with { Authenticated = false }, client with { NetworkLogon = true }, client with { Anonymous = true } })
            Reject<UnauthorizedAccessException>(() => InstalledWorkerAdmission.RequireOwnerClient(binding, changed), "nonowner or remote/anonymous caller accepted");
        var userBinding = new InstalledUserWorkerBinding(1, binding.BindingId, owner,
            binding.WorkerExecutable, binding.DataDirectory);
        var userArguments = new[] { "--installed-user-pipe", "--data", binding.DataDirectory,
            "--pipe-name", "Pegasus.Cerebral.User." + binding.BindingId,
            "--pipe-instance", "abcdefabcdefabcdefabcdefabcdefab" };
        InstalledUserWorkerAdmission.RequireStartupArguments(userArguments); checks++;
        Reject<ArgumentException>(() => InstalledUserWorkerAdmission.RequireStartupArguments(
            [.. userArguments, "--installed-pipe"]), "mixed installed host modes accepted");
        Reject<ArgumentException>(() => InstalledUserWorkerAdmission.RequireStartupArguments(
            [.. userArguments, "--data", @"C:\Other"]), "duplicate desktop data argument accepted");
        Check(InstalledUserWorkerBinding.Parse(Encoding.UTF8.GetBytes(userBinding.ToJson())) == userBinding,
            "desktop enrollment round trips without service registration fields");
        Check(InstalledWorkerBindingStore.UserBindingValueName != InstalledWorkerBindingStore.BindingValueName,
            "desktop enrollment has a distinct protected registry value");
        Reject<InvalidDataException>(() => InstalledUserWorkerBinding.Parse(Encoding.UTF8.GetBytes(
            userBinding.ToJson().TrimEnd('}') + ",\"serviceName\":\"PegasusCerebral\"}")),
            "service-shaped fields accepted by desktop enrollment");
        InstalledUserWorkerAdmission.RequireLegacyTransition(binding, userBinding); checks++;
        Reject<UnauthorizedAccessException>(() => InstalledUserWorkerAdmission.RequireLegacyTransition(binding,
            userBinding with { DataDirectory = @"C:\Other\Data" }), "different durable data root accepted during desktop transition");
        Reject<UnauthorizedAccessException>(() => InstalledUserWorkerAdmission.RequireLegacyTransition(binding,
            userBinding with { OwnerSid = "S-1-5-21-100-200-300-1002" }), "different owner accepted during desktop transition");
        Reject<UnauthorizedAccessException>(() => InstalledUserWorkerAdmission.RequireLegacyTransition(binding,
            userBinding with { BindingId = Guid.NewGuid().ToString("N") }), "different installation accepted during desktop transition");
        var userProcess = new InstalledWorkerProcessIdentity(4567, owner, userBinding.WorkerExecutable, 1);
        InstalledUserWorkerAdmission.RequireHost(userBinding, userProcess); checks++;
        InstalledUserWorkerAdmission.RequireOwnerClient(userBinding,
            new InstalledWorkerProcessIdentity(8910, owner, @"C:\Program Files\Pegasus\Cerebral\Applications\Desktop\Cerebral.Orchestrator.exe", 1)); checks++;
        foreach (var changed in new[] { userProcess with { ProcessId = 0 }, userProcess with { SessionId = 0 },
            userProcess with { AccountSid = binding.ServiceAccountSid },
            userProcess with { Executable = @"C:\Other\Cerebral.Worker.exe" } })
            Reject<UnauthorizedAccessException>(() => InstalledUserWorkerAdmission.RequireHost(userBinding, changed),
                "nonowner desktop Worker host accepted");
        Reject<UnauthorizedAccessException>(() => InstalledUserWorkerAdmission.RequireOwnerClient(userBinding,
            userProcess with { AccountSid = binding.ServiceAccountSid }), "service account accepted as desktop client");
        var desktopCaller = new InstalledWorkerClientIdentity(owner, 1, true, false, false);
        InstalledUserWorkerAdmission.RequireOwnerClient(userBinding, desktopCaller, 1); checks++;
        foreach (var changed in new[] { desktopCaller with { SessionId = 2 },
            desktopCaller with { UserSid = binding.ServiceAccountSid }, desktopCaller with { Authenticated = false },
            desktopCaller with { NetworkLogon = true }, desktopCaller with { Anonymous = true } })
            Reject<UnauthorizedAccessException>(() => InstalledUserWorkerAdmission.RequireOwnerClient(userBinding,
                changed, 1), "wrong installed desktop caller admitted");
        var userEndpoint = new LocalWorkerEndpoint("Pegasus.Cerebral.User." + binding.BindingId,
            "abcdefabcdefabcdefabcdefabcdefab");
        InstalledUserWorkerAdmission.RequireEndpoint(userBinding, userEndpoint); checks++;
        Reject<UnauthorizedAccessException>(() => InstalledUserWorkerAdmission.RequireEndpoint(userBinding,
            userEndpoint with { PipeName = binding.PipeEndpoint().PipeName }), "service endpoint accepted for desktop mode");
        Reject<ArgumentException>(() => InstalledUserWorkerAdmission.RequireEndpoint(userBinding,
            userEndpoint with { InstanceId = Guid.Empty.ToString("N") }), "empty desktop generation accepted");
        var expectedUser = new LocalWorkerExpectedOwner(userEndpoint, 4567, 123456789,
            binding.WorkerExecutable, owner, 1, binding.DataDirectory,
            [binding.WorkerExecutable, "--installed-user-pipe", "--data", binding.DataDirectory,
                "--pipe-name", userEndpoint.PipeName, "--pipe-instance", userEndpoint.InstanceId]);
        Check(expectedUser.Endpoint == userEndpoint, "exact installed-user native arguments admitted");
        Reject<ArgumentException>(() => new LocalWorkerExpectedOwner(userEndpoint, 4567, 123456789,
            binding.WorkerExecutable, owner, 1, binding.DataDirectory,
            [binding.WorkerExecutable, "--installed-user-pipe", "--data", binding.DataDirectory,
                "--pipe-name", userEndpoint.PipeName, "--pipe-instance", userEndpoint.InstanceId,
                "--service-plugins", @"C:\Other"]), "installed user mode accepted injected code path");
        Console.WriteLine($"Installed Worker binding: {checks} synthetic document, ACL and identity-policy assertions passed. No installed enrollment, Windows pipe authentication or service was exercised.");
    }
}
