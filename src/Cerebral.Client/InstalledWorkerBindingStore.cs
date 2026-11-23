using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Text;
using Cerebral.Core;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Cerebral.Client;

/// <summary>Read-only machine enrollment. User preferences, arbitrary files and command arguments
/// cannot replace this authority source. No enrollment or transport activation is performed here.</summary>
public static class InstalledWorkerBindingStore
{
    public const string RegistryPath = @"Software\Pegasus\Cerebral\LocalTransport";
    public const string BindingValueName = "BindingJson";
    public const string UserBindingValueName = "UserBindingJson";
    private const uint RegSz = 1;
    private const int MaximumRegistryBytes = InstalledWorkerBinding.MaximumDocumentBytes * 2 + 2;
    private static readonly UnicodeEncoding StrictUnicode = new(false, false, true);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static InstalledWorkerBinding? ReadRegistered() =>
        ReadRegisteredValue(BindingValueName, static bytes => InstalledWorkerBinding.Parse(bytes), allowMissingValue: false);

    /// <summary>Independent desktop enrollment. Absence is a provisioning gate; the service
    /// document is never interpreted as a substitute for this value.</summary>
    public static InstalledUserWorkerBinding? ReadRegisteredUser() =>
        ReadRegisteredValue(UserBindingValueName, static bytes => InstalledUserWorkerBinding.Parse(bytes), allowMissingValue: true);

    private static T? ReadRegisteredValue<T>(string valueName, Func<byte[], T> parse, bool allowMissingValue) where T : class
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Installed Worker enrollment requires Windows.");
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(RegistryPath, writable: false);
        if (key is null) return null;
        ValidateProtection(key.Handle);
        uint size = 0;
        var status = RegQueryValueEx(key.Handle, valueName, IntPtr.Zero, out var type, null, ref size);
        if (status == 2 && allowMissingValue) return null;
        if (status != 0) throw ReadError(status);
        if (type != RegSz || size is < 4 or > MaximumRegistryBytes || (size & 1) != 0)
            throw new InvalidDataException("Installed Worker enrollment must contain a bounded REG_SZ document.");
        var bytes = new byte[checked((int)size)];
        status = RegQueryValueEx(key.Handle, valueName, IntPtr.Zero, out type, bytes, ref size);
        if (status != 0) throw ReadError(status);
        if (type != RegSz || size != bytes.Length || bytes[^1] != 0 || bytes[^2] != 0)
            throw new InvalidDataException("Installed Worker enrollment changed or has invalid string framing. Read it again before connecting.");
        try
        {
            var value = StrictUnicode.GetString(bytes, 0, bytes.Length - 2);
            if (value.Contains('\0')) throw new InvalidDataException("Installed Worker enrollment contains invalid string framing.");
            var binding = parse(StrictUtf8.GetBytes(value));
            // Recheck protection on the same open key before returning the document.
            ValidateProtection(key.Handle);
            return binding;
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("Installed Worker enrollment contains invalid Unicode.");
        }
        catch (EncoderFallbackException)
        {
            throw new InvalidDataException("Installed Worker enrollment contains invalid Unicode.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateProtection(SafeRegistryHandle handle)
    {
        // GetSecurityInfo is used directly to avoid adding a registry-access-control package.
        var result = GetSecurityInfo(handle, 4 /* SE_REGISTRY_KEY */, 5 /* OWNER | DACL */,
            out _, out _, out _, out _, out var descriptor);
        if (result != 0) throw new UnauthorizedAccessException("Installed Worker enrollment protection could not be verified.", new Win32Exception(checked((int)result)));
        try
        {
            var length = GetSecurityDescriptorLength(descriptor);
            if (length is < 20 or > 65536) throw new InvalidDataException("Installed Worker enrollment security descriptor is invalid or oversized.");
            var bytes = new byte[checked((int)length)]; Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var security = new RawSecurityDescriptor(bytes, 0);
            var entries = new List<InstalledWorkerRegistryAce>();
            if (security.DiscretionaryAcl is { } acl)
            {
                if (acl.Count > 128) throw new UnauthorizedAccessException("Installed Worker enrollment has an unsupported access policy.");
                foreach (var ace in acl)
                {
                    // Conditional/object-specific ACEs are outside the fixed enrollment ACL contract.
                    if (ace is not CommonAce common || common.IsCallback || common.AceQualifier is not
                        (AceQualifier.AccessAllowed or AceQualifier.AccessDenied))
                        throw new UnauthorizedAccessException("Installed Worker enrollment has an unsupported access policy.");
                    entries.Add(new(common.SecurityIdentifier.Value, unchecked((uint)common.AccessMask), common.AceQualifier == AceQualifier.AccessAllowed));
                }
            }
            InstalledWorkerAdmission.RequireProtectedRegistry(new(security.Owner?.Value ?? "",
                security.DiscretionaryAcl is not null && security.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclPresent),
                security.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected), entries));
        }
        finally { if (descriptor != IntPtr.Zero) _ = LocalFree(descriptor); }
    }

    private static IOException ReadError(int status) => new("Installed Worker enrollment is incomplete, unavailable or changed during reading.", new Win32Exception(status));

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryValueExW")]
    private static extern int RegQueryValueEx(SafeRegistryHandle key, string name, IntPtr reserved, out uint type,
        [Out] byte[]? data, ref uint size);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeRegistryHandle handle, int objectType, uint information,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr pointer);
}
