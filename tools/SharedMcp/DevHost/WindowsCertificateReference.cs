using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

/// <summary>Read-only selection of an already-provisioned certificate; this host never installs trust or exports keys.</summary>
sealed class WindowsCertificateReference
{
    public string Location { get; init; } = "CurrentUser";
    public string Store { get; init; } = "My";
    public required string CertificateId { get; init; }

    /// <summary>Pure reference admission. Public machine CA selection never admits a machine private key.</summary>
    public (StoreLocation Location, StoreName Store) Admit(bool requirePrivateKey)
    {
        var allowed = requirePrivateKey
            ? Location == "CurrentUser" && Store == "My"
            : (Location == "CurrentUser" && Store is ("My" or "Root" or "CertificateAuthority")) ||
                (Location == "LocalMachine" && Store == "Root");
        if (!allowed || CertificateId is null || CertificateId.Length != 64 ||
            CertificateId.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new CertificateReferenceException("certificate_reference_invalid");
        return (Enum.Parse<StoreLocation>(Location), Enum.Parse<StoreName>(Store));
    }

    public X509Certificate2 Resolve(bool requirePrivateKey)
    {
        var selection = Admit(requirePrivateKey);
        if (!OperatingSystem.IsWindows()) throw new CertificateReferenceException("certificate_store_windows_required");
        try
        {
            using var store = new X509Store(selection.Store, selection.Location);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var certificates = store.Certificates;
            try
            {
                var matches = certificates.Where(c => c.GetCertHashString(HashAlgorithmName.SHA256)
                    .Equals(CertificateId, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1) throw new CertificateReferenceException("certificate_reference_missing_or_ambiguous");
                if (requirePrivateKey && !matches[0].HasPrivateKey) throw new CertificateReferenceException("certificate_private_key_unavailable");
                // The public CA selection drops any private-key association. The server borrows an owned duplicated handle.
                return requirePrivateKey ? new X509Certificate2(matches[0]) : X509CertificateLoader.LoadCertificate(matches[0].RawData);
            }
            finally { foreach (var certificate in certificates) certificate.Dispose(); }
        }
        catch (CryptographicException) { throw new CertificateReferenceException("certificate_store_access_failed"); }
    }
}

sealed class CertificateReferenceException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
