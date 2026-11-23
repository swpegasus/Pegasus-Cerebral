using System.Runtime.Versioning;
using Cerebral.Core;

namespace Cerebral.Client;

public sealed record InstalledWorkerRegistrationPlan(InstalledWorkerEnrollmentPlan Enrollment,
    string ServiceCommandLine);

/// <summary>Pure preparation for the installed owner-pipe service. Applying the plan remains an
/// explicit elevated management action; preparation does not touch SCM, registry, ACLs or files.</summary>
[SupportedOSPlatform("windows")]
public static class InstalledWorkerRegistration
{
    public static InstalledWorkerRegistrationPlan Prepare(string ownerSid, string workerExecutable,
        string dataDirectory, InstalledWorkerBinding? existing = null)
    {
        var enrollment = InstalledWorkerEnrollment.Prepare(ownerSid, workerExecutable, dataDirectory, existing);
        var binding = enrollment.Binding;
        return new(enrollment, Quote(binding.WorkerExecutable) + " --data " + Quote(binding.DataDirectory) + " --installed-pipe");
    }

    private static string Quote(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('"') || value.Contains('\r') || value.Contains('\n'))
            throw new ArgumentException("Installed service paths cannot be empty or contain command-line delimiters.");
        var trailingSlashes = value.Length - value.TrimEnd('\\').Length;
        return "\"" + value + new string('\\', trailingSlashes) + "\"";
    }
}
