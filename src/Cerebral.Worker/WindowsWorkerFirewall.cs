using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cerebral.Worker;

internal sealed record WorkerFirewallPlan(
    string RuleName,
    string RuleGroup,
    string ProgramPath,
    int LocalPort,
    int Profiles,
    string RemoteAddresses)
{
    internal const string OwnedRuleName = "Pegasus Cerebral Worker";
    internal const string OwnedRuleGroup = "Pegasus Cerebral";
    internal const int PrivateProfile = 2;

    internal static WorkerFirewallPlan Create(string programPath, string listenUrl)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Firewall provisioning is available only on Windows.");
        var program = Path.GetFullPath(programPath);
        if (!File.Exists(program) ||
            !Path.GetFileName(program).Equals("Cerebral.Worker.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The firewall rule must be bound to the installed Cerebral.Worker.exe.");
        if (!Uri.TryCreate(listenUrl, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttp || endpoint.Host != "0.0.0.0" ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.AbsolutePath.Trim('/').Length != 0 || endpoint.Port is < 1 or > 65535)
            throw new ArgumentException("The remote Worker firewall rule requires its finalized IPv4 wildcard HTTP origin.");
        return new(OwnedRuleName, OwnedRuleGroup, program, endpoint.Port, PrivateProfile, "LocalSubnet");
    }
}

[SupportedOSPlatform("windows")]
internal static class WindowsWorkerFirewall
{
    private const int TcpProtocol = 6;
    private const int InboundDirection = 1;
    private const int AllowAction = 1;

    internal static void Apply(WorkerFirewallPlan plan)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Firewall provisioning is available only on Windows.");
        object? policyObject = null;
        object? rulesObject = null;
        object? existingObject = null;
        object? ruleObject = null;
        object? observedObject = null;
        var added = false;
        try
        {
            policyObject = CreateCom("HNetCfg.FwPolicy2");
            dynamic policy = policyObject;
            rulesObject = policy.Rules;
            dynamic rules = rulesObject;
            existingObject = TryFind(rules, plan.RuleName);
            if (existingObject is not null)
            {
                dynamic existing = existingObject;
                EnsureOwned(existing, plan.ProgramPath);
                rules.Remove(plan.RuleName);
            }

            ruleObject = CreateCom("HNetCfg.FWRule");
            dynamic rule = ruleObject;
            rule.Name = plan.RuleName;
            rule.Description = "Allows an authenticated Pegasus Cerebral controller on the local private network to reach this Worker.";
            rule.ApplicationName = plan.ProgramPath;
            rule.Protocol = TcpProtocol;
            rule.LocalPorts = plan.LocalPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            rule.Direction = InboundDirection;
            rule.Enabled = true;
            rule.Grouping = plan.RuleGroup;
            rule.Profiles = plan.Profiles;
            rule.Action = AllowAction;
            rule.EdgeTraversal = false;
            rule.RemoteAddresses = plan.RemoteAddresses;
            rules.Add(rule);
            added = true;

            observedObject = TryFind(rules, plan.RuleName)
                ?? throw new IOException("Windows Firewall did not retain the Worker rule.");
            dynamic observed = observedObject;
            EnsureOwned(observed, plan.ProgramPath);
            if (!(bool)observed.Enabled || (int)observed.Protocol != TcpProtocol ||
                (int)observed.Direction != InboundDirection || (int)observed.Action != AllowAction ||
                (int)observed.Profiles != plan.Profiles ||
                !string.Equals((string)observed.LocalPorts, plan.LocalPort.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
                throw new IOException("The retained Windows Firewall rule does not match the requested private Worker policy.");
        }
        catch
        {
            if (added && rulesObject is not null)
            {
                try { ((dynamic)rulesObject).Remove(plan.RuleName); }
                catch (COMException) { }
            }
            throw;
        }
        finally
        {
            Release(observedObject);
            Release(ruleObject);
            Release(existingObject);
            Release(rulesObject);
            Release(policyObject);
        }
    }

    internal static bool Remove(string programPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Firewall provisioning is available only on Windows.");
        var program = Path.GetFullPath(programPath);
        object? policyObject = null;
        object? rulesObject = null;
        object? existingObject = null;
        try
        {
            policyObject = CreateCom("HNetCfg.FwPolicy2");
            dynamic policy = policyObject;
            rulesObject = policy.Rules;
            dynamic rules = rulesObject;
            existingObject = TryFind(rules, WorkerFirewallPlan.OwnedRuleName);
            if (existingObject is null) return false;
            dynamic existing = existingObject;
            EnsureOwned(existing, program);
            rules.Remove(WorkerFirewallPlan.OwnedRuleName);
            return true;
        }
        finally
        {
            Release(existingObject);
            Release(rulesObject);
            Release(policyObject);
        }
    }

    internal static bool Exists()
    {
        object? policyObject = null;
        object? rulesObject = null;
        object? existingObject = null;
        try
        {
            policyObject = CreateCom("HNetCfg.FwPolicy2");
            dynamic policy = policyObject;
            rulesObject = policy.Rules;
            dynamic rules = rulesObject;
            existingObject = TryFind(rules, WorkerFirewallPlan.OwnedRuleName);
            return existingObject is not null;
        }
        finally
        {
            Release(existingObject);
            Release(rulesObject);
            Release(policyObject);
        }
    }

    private static object CreateCom(string programId)
    {
        var type = Type.GetTypeFromProgID(programId, throwOnError: true)
            ?? throw new PlatformNotSupportedException("The Windows Firewall COM API is unavailable.");
        return Activator.CreateInstance(type)
            ?? throw new PlatformNotSupportedException("The Windows Firewall COM API could not be activated.");
    }

    private static object? TryFind(dynamic rules, string name)
    {
        try { return rules.Item(name); }
        catch (COMException) { return null; }
        catch (FileNotFoundException) { return null; }
    }

    private static void EnsureOwned(dynamic rule, string expectedProgram)
    {
        var application = (string?)rule.ApplicationName;
        var group = (string?)rule.Grouping;
        if (string.IsNullOrWhiteSpace(application) ||
            !Path.GetFullPath(application).Equals(Path.GetFullPath(expectedProgram), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(group, WorkerFirewallPlan.OwnedRuleGroup, StringComparison.Ordinal))
            throw new InvalidOperationException("A Windows Firewall rule with the Cerebral Worker name exists but is not owned by this installation.");
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}
