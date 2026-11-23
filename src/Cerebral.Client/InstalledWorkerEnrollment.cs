using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Cerebral.Core;
using Microsoft.Win32.SafeHandles;

namespace Cerebral.Client;

public sealed record InstalledWorkerOwnerAccount(string Sid, string DisplayName);

public sealed record InstalledWorkerEnrollmentPlan(InstalledWorkerBinding Binding,
    InstalledWorkerOwnerAccount Owner, bool PreservesBindingId);

/// <summary>Elevated setup primitive for the installed owner pipe. Callers must present and confirm
/// the intended individual account; this class never infers it from the elevated process token.</summary>
[SupportedOSPlatform("windows")]
public static class InstalledWorkerEnrollment
{
    private const uint KeyQueryValue = 0x0001;
    private const uint KeySetValue = 0x0002;
    private const uint KeyWow64_64Key = 0x0100;
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;
    private const uint RegSz = 1;
    private static readonly IntPtr LocalMachine = new(unchecked((int)0x80000002));
    private static readonly UnicodeEncoding StrictUnicode = new(false, false, true);

    public static InstalledWorkerEnrollmentPlan Prepare(string ownerSid, string workerExecutable,
        string dataDirectory, InstalledWorkerBinding? existing = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Installed Worker enrollment requires Windows.");
        var owner = ResolveOwner(ownerSid);
        existing?.Validate();
        if (existing is not null && existing.OwnerSid != owner.Sid)
            throw new InvalidOperationException("Changing the installed Cerebral owner requires a separate explicit owner-change operation.");
        var binding = new InstalledWorkerBinding(InstalledWorkerBinding.CurrentVersion,
            existing?.BindingId ?? Guid.NewGuid().ToString("N"), owner.Sid,
            InstalledWorkerBinding.ExpectedServiceName, InstalledWorkerBinding.LocalServiceSid,
            Path.GetFullPath(workerExecutable), Path.GetFullPath(dataDirectory));
        binding.Validate();
        return new(binding, owner, existing is not null);
    }

    public static InstalledWorkerOwnerAccount ResolveOwner(string ownerSid)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Installed Worker enrollment requires Windows.");
        SecurityIdentifier sid;
        try { sid = new SecurityIdentifier(ownerSid); }
        catch (Exception error) when (error is ArgumentException or SystemException)
        { throw new ArgumentException("Select a canonical Windows user SID for the Cerebral owner.", nameof(ownerSid), error); }
        if (sid.Value != ownerSid) throw new ArgumentException("Select a canonical Windows user SID for the Cerebral owner.", nameof(ownerSid));
        var bytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(bytes, 0);
        uint nameLength = 0, domainLength = 0;
        _ = LookupAccountSid(null, bytes, null, ref nameLength, null, ref domainLength, out _);
        if (Marshal.GetLastWin32Error() != 122 || nameLength is < 1 or > 1024 || domainLength > 1024)
            throw NativeFailure("The selected Cerebral owner account could not be resolved");
        var name = new StringBuilder(checked((int)nameLength));
        var domain = new StringBuilder(checked((int)Math.Max(domainLength, 1)));
        if (!LookupAccountSid(null, bytes, name, ref nameLength, domain, ref domainLength, out var use))
            throw NativeFailure("The selected Cerebral owner account could not be resolved");
        if (use != 1 /* SidTypeUser */)
            throw new ArgumentException("The installed Cerebral owner must be an individual Windows user account.", nameof(ownerSid));
        var display = domain.Length == 0 ? name.ToString() : domain + "\\" + name.ToString();
        if (string.IsNullOrWhiteSpace(display) || display.Length > 2048 || display.Any(char.IsControl))
            throw new InvalidDataException("The selected Cerebral owner account name is invalid or oversized.");
        // Binding validation also rejects service, built-in, group and shortened account SID shapes.
        var probe = new InstalledWorkerBinding(InstalledWorkerBinding.CurrentVersion,
            "11111111111111111111111111111111", sid.Value, InstalledWorkerBinding.ExpectedServiceName,
            InstalledWorkerBinding.LocalServiceSid, @"C:\Pegasus\Cerebral\Worker\Cerebral.Worker.exe", @"C:\Pegasus\CerebralData");
        probe.Validate();
        return new(sid.Value, display);
    }

    /// <summary>Writes one protected nonsecret binding. This does not configure, start or stop the service.</summary>
    public static InstalledWorkerBinding WriteRegistered(InstalledWorkerEnrollmentPlan plan)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Installed Worker enrollment requires Windows.");
        ArgumentNullException.ThrowIfNull(plan);
        plan.Binding.Validate();
        var resolved = ResolveOwner(plan.Binding.OwnerSid);
        if (resolved != plan.Owner)
            throw new InvalidDataException("The confirmed Cerebral owner no longer resolves to the prepared account.");
        if (!File.Exists(plan.Binding.WorkerExecutable) ||
            (File.GetAttributes(plan.Binding.WorkerExecutable) & FileAttributes.ReparsePoint) != 0)
            throw new FileNotFoundException("The installed Cerebral Worker must be an existing plain executable file.", plan.Binding.WorkerExecutable);
        if (!Directory.Exists(plan.Binding.DataDirectory) ||
            (File.GetAttributes(plan.Binding.DataDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new DirectoryNotFoundException("The installed Cerebral data directory must be an existing plain directory.");
        var current = InstalledWorkerBindingStore.ReadRegistered();
        if (current is null && plan.PreservesBindingId)
            throw new InvalidOperationException("The prepared Cerebral upgrade binding no longer exists. Prepare enrollment again.");
        if (current is not null && (!plan.PreservesBindingId || current.BindingId != plan.Binding.BindingId || current.OwnerSid != plan.Binding.OwnerSid))
            throw new InvalidOperationException("Installed Cerebral enrollment changed. Owner replacement and binding rotation require a separate explicit operation.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Writing installed Cerebral enrollment requires elevated setup.");

        var descriptor = IntPtr.Zero;
        SafeRegistryHandle? key = null;
        try
        {
            var sddl = "O:BAG:BAD:P(A;;KA;;;SY)(A;;KA;;;BA)(A;;KR;;;LS)(A;;KR;;;" + plan.Binding.OwnerSid + ")";
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out descriptor, out var descriptorBytes)
                || descriptor == IntPtr.Zero || descriptorBytes is < 20 or > 65536)
                throw NativeFailure("Windows could not prepare the protected Cerebral enrollment policy");
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(), SecurityDescriptor = descriptor, InheritHandle = false
            };
            var status = RegCreateKeyEx(LocalMachine, InstalledWorkerBindingStore.RegistryPath, 0, null, 0,
                KeyQueryValue | KeySetValue | KeyWow64_64Key | ReadControl | WriteDac | WriteOwner,
                ref attributes, out key, out _);
            if (status != 0) throw RegistryFailure("The protected Cerebral enrollment key could not be opened", status);
            status = RegSetKeySecurity(key, OwnerSecurityInformation | DaclSecurityInformation | ProtectedDaclSecurityInformation, descriptor);
            if (status != 0) throw RegistryFailure("The protected Cerebral enrollment policy could not be applied", status);
            var text = plan.Binding.ToJson();
            var value = StrictUnicode.GetBytes(text + '\0');
            if (value.Length > InstalledWorkerBinding.MaximumDocumentBytes * 2 + 2)
                throw new InvalidDataException("Installed Worker enrollment is oversized.");
            status = RegSetValueEx(key, InstalledWorkerBindingStore.BindingValueName, 0, RegSz, value, value.Length);
            if (status != 0) throw RegistryFailure("The protected Cerebral enrollment document could not be written", status);
            status = RegFlushKey(key);
            if (status != 0) throw RegistryFailure("The protected Cerebral enrollment document could not be committed", status);
        }
        finally
        {
            key?.Dispose();
            if (descriptor != IntPtr.Zero) _ = LocalFree(descriptor);
        }
        var verified = InstalledWorkerBindingStore.ReadRegistered()
            ?? throw new IOException("The installed Cerebral enrollment was not readable after writing.");
        if (verified != plan.Binding) throw new IOException("The installed Cerebral enrollment changed while it was being verified.");
        return verified;
    }

    private static Win32Exception NativeFailure(string message) => new(Marshal.GetLastWin32Error(), message + ".");
    private static IOException RegistryFailure(string message, int status) => new(message + ".", new Win32Exception(status));

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LookupAccountSidW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupAccountSid(string? systemName, byte[] sid, StringBuilder? name,
        ref uint nameLength, StringBuilder? domain, ref uint domainLength, out int use);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision,
        out IntPtr descriptor, out uint descriptorBytes);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegCreateKeyExW")]
    private static extern int RegCreateKeyEx(IntPtr root, string subKey, int reserved, string? keyClass, uint options,
        uint desiredAccess, ref SecurityAttributes attributes, out SafeRegistryHandle result, out uint disposition);
    [DllImport("advapi32.dll", EntryPoint = "RegSetKeySecurity")]
    private static extern int RegSetKeySecurity(SafeRegistryHandle key, uint securityInformation, IntPtr descriptor);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegSetValueExW")]
    private static extern int RegSetValueEx(SafeRegistryHandle key, string valueName, int reserved, uint type,
        byte[] data, int size);
    [DllImport("advapi32.dll", EntryPoint = "RegFlushKey")]
    private static extern int RegFlushKey(SafeRegistryHandle key);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr pointer);
}
