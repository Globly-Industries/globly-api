using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Globly.Api.Posts;

public class AttestationOptions
{
    public string PackageName { get; set; } = "com.globly.globly";

    /// <summary>Hex SHA-256 of trusted APK signing certificates. Empty: nothing can be verified.</summary>
    public string[] SigningCertSha256 { get; set; } = [];

    /// <summary>Require a locked bootloader and verified system (rooted phones can fake captures).</summary>
    public bool RequireLockedDevice { get; set; } = true;
}

public enum AttestationProblem
{
    None,
    NotConfigured,
    Malformed,
    UntrustedChain,
    Revoked,
    WrongChallenge,
    SoftwareOnly,
    ImportedKey,
    WrongApp,
    UnlockedDevice,
}

/// <summary>
/// Verifies Android key attestation: proof that our signed app on a real phone made a key for this challenge.
/// See https://developer.android.com/privacy-and-security/security-key-attestation
/// </summary>
public static class AndroidAttestation
{
    /// <summary>SPKI SHA-256 of Google's roots (https://android.googleapis.com/attestation/root).</summary>
    public static readonly IReadOnlyCollection<string> GoogleRootKeys =
    [
        "feb2ea7551ee316ed4bb443c8293b884dbfdea40b603ee3e4f4a897e4580fbae",
        "3ee44512a1af2beb39c889490c60ea3f82e43f5d5a5532f5ab9419f676cd07ec",
    ];

    private const string KeyDescriptionOid = "1.3.6.1.4.1.11129.2.1.17";

    private const int TagOrigin = 702;
    private const int TagRootOfTrust = 704;
    private const int TagApplicationId = 709;

    /// <summary>Binds ticket, file and location. Must match the app's captureChallenge.</summary>
    public static byte[] Challenge(Guid captureId, string sha256, double latitude, double longitude)
    {
        static long E6(double degrees) => (long)Math.Round(degrees * 1_000_000, MidpointRounding.AwayFromZero);
        var text = string.Create(CultureInfo.InvariantCulture,
            $"globly-capture|{captureId:D}|{sha256}|{E6(latitude)}|{E6(longitude)}");
        return SHA256.HashData(Encoding.UTF8.GetBytes(text));
    }

    public static string? NormalizeDigest(string value)
    {
        var hex = value.Replace(":", "").Replace(" ", "").ToLowerInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    /// <param name="chain">DER certificates, leaf first.</param>
    public static AttestationProblem Verify(
        IReadOnlyList<byte[]> chain, byte[] challenge, AttestationOptions options,
        IReadOnlyCollection<string> rootKeys, Func<string, bool> isRevoked)
    {
        var digests = options.SigningCertSha256.Select(NormalizeDigest).OfType<string>().ToHashSet();
        if (digests.Count == 0) return AttestationProblem.NotConfigured;
        if (chain.Count < 2 || chain.Count > 10) return AttestationProblem.Malformed;

        var certs = new List<X509Certificate2>();
        try
        {
            foreach (var der in chain) certs.Add(X509CertificateLoader.LoadCertificate(der));

            // Each cert signed by the next; the root by itself.
            for (var i = 0; i < certs.Count; i++)
            {
                var issuer = certs[Math.Min(i + 1, certs.Count - 1)];
                if (!IsSignedBy(chain[i], issuer)) return AttestationProblem.UntrustedChain;
            }
            var rootKey = Convert.ToHexStringLower(SHA256.HashData(certs[^1].PublicKey.ExportSubjectPublicKeyInfo()));
            if (!rootKeys.Contains(rootKey)) return AttestationProblem.UntrustedChain;

            foreach (var cert in certs)
                if (isRevoked(SerialHex(cert))) return AttestationProblem.Revoked;

            var extension = certs[0].Extensions[KeyDescriptionOid];
            if (extension is null) return AttestationProblem.Malformed;
            return CheckKeyDescription(extension.RawData, challenge, options, digests);
        }
        catch (Exception e) when (e is CryptographicException or AsnContentException or FormatException)
        {
            return AttestationProblem.Malformed;
        }
        finally
        {
            foreach (var cert in certs) cert.Dispose();
        }
    }

    /// <summary>Serial in the revocation list's format.</summary>
    public static string SerialHex(X509Certificate2 cert)
    {
        var hex = Convert.ToHexStringLower(cert.SerialNumberBytes.Span).TrimStart('0');
        return hex.Length == 0 ? "0" : hex;
    }

    private static AttestationProblem CheckKeyDescription(
        byte[] der, byte[] challenge, AttestationOptions options, HashSet<string> digests)
    {
        // BER: some devices don't write strict DER.
        var description = new AsnReader(der, AsnEncodingRules.BER).ReadSequence();
        description.ReadInteger(); // attestationVersion
        var attestationLevel = ReadSecurityLevel(description);
        description.ReadInteger(); // keyMintVersion
        var keyLevel = ReadSecurityLevel(description);
        var keyChallenge = description.ReadOctetString();
        description.ReadOctetString(); // uniqueId
        var softwareEnforced = description.ReadSequence();
        var hardwareEnforced = description.ReadSequence();

        if (!CryptographicOperations.FixedTimeEquals(keyChallenge, challenge)) return AttestationProblem.WrongChallenge;
        if (attestationLevel < 1 || keyLevel < 1) return AttestationProblem.SoftwareOnly;

        var hardware = ReadAuthorizations(hardwareEnforced);
        var software = ReadAuthorizations(softwareEnforced);

        // 0 = generated in hardware, not imported.
        if (!hardware.TryGetValue(TagOrigin, out var origin) || new AsnReader(origin, AsnEncodingRules.BER).ReadInteger() != 0)
            return AttestationProblem.ImportedKey;

        var appId = software.GetValueOrDefault(TagApplicationId) ?? hardware.GetValueOrDefault(TagApplicationId);
        if (appId is null || !IsTrustedApp(appId, options.PackageName, digests)) return AttestationProblem.WrongApp;

        if (options.RequireLockedDevice)
        {
            if (!hardware.TryGetValue(TagRootOfTrust, out var rootOfTrust)) return AttestationProblem.UnlockedDevice;
            var root = new AsnReader(rootOfTrust, AsnEncodingRules.BER).ReadSequence();
            root.ReadOctetString(); // verifiedBootKey
            var deviceLocked = root.ReadBoolean();
            var bootState = root.ReadEnumeratedValue<VerifiedBootState>();
            if (!deviceLocked || bootState != VerifiedBootState.Verified) return AttestationProblem.UnlockedDevice;
        }
        return AttestationProblem.None;
    }

    private enum VerifiedBootState { Verified = 0, SelfSigned = 1, Unverified = 2, Failed = 3 }

    private static int ReadSecurityLevel(AsnReader reader) =>
        (int)reader.ReadEnumeratedValue<SecurityLevel>();

    private enum SecurityLevel { Software = 0, TrustedEnvironment = 1, StrongBox = 2 }

    private static Dictionary<int, byte[]> ReadAuthorizations(AsnReader list)
    {
        var entries = new Dictionary<int, byte[]>();
        while (list.HasData)
        {
            var tag = list.PeekTag();
            var field = list.ReadSequence(tag);
            if (tag.TagClass == TagClass.ContextSpecific && field.HasData)
                entries[tag.TagValue] = field.ReadEncodedValue().ToArray();
        }
        return entries;
    }

    private static bool IsTrustedApp(byte[] encoded, string packageName, HashSet<string> digests)
    {
        var appId = new AsnReader(new AsnReader(encoded, AsnEncodingRules.BER).ReadOctetString(), AsnEncodingRules.BER)
            .ReadSequence();

        var packages = appId.ReadSetOf(skipSortOrderValidation: true);
        var hasPackage = false;
        while (packages.HasData)
        {
            var info = packages.ReadSequence();
            if (Encoding.UTF8.GetString(info.ReadOctetString()) == packageName) hasPackage = true;
        }

        var signatures = appId.ReadSetOf(skipSortOrderValidation: true);
        var signers = new List<string>();
        while (signatures.HasData) signers.Add(Convert.ToHexStringLower(signatures.ReadOctetString()));

        return hasPackage && signers.Count > 0 && signers.All(digests.Contains);
    }

    private static bool IsSignedBy(byte[] certificate, X509Certificate2 issuer)
    {
        // BER: some TEEs write leaf certs that aren't strict DER; the signed bytes are taken as-is.
        var cert = new AsnReader(certificate, AsnEncodingRules.BER).ReadSequence();
        var tbs = cert.ReadEncodedValue().ToArray();
        var algorithm = cert.ReadSequence().ReadObjectIdentifier();
        var signature = cert.ReadBitString(out _);

        if (!SignatureAlgorithms.TryGetValue(algorithm, out var alg)) return false;
        if (alg.Rsa)
        {
            using var rsa = issuer.GetRSAPublicKey();
            return rsa is not null && rsa.VerifyData(tbs, signature, alg.Hash, RSASignaturePadding.Pkcs1);
        }
        using var ecdsa = issuer.GetECDsaPublicKey();
        return ecdsa is not null && ecdsa.VerifyData(tbs, signature, alg.Hash, DSASignatureFormat.Rfc3279DerSequence);
    }

    private static readonly Dictionary<string, (bool Rsa, HashAlgorithmName Hash)> SignatureAlgorithms = new()
    {
        ["1.2.840.113549.1.1.11"] = (true, HashAlgorithmName.SHA256),
        ["1.2.840.113549.1.1.12"] = (true, HashAlgorithmName.SHA384),
        ["1.2.840.113549.1.1.13"] = (true, HashAlgorithmName.SHA512),
        ["1.2.840.10045.4.3.2"] = (false, HashAlgorithmName.SHA256),
        ["1.2.840.10045.4.3.3"] = (false, HashAlgorithmName.SHA384),
        ["1.2.840.10045.4.3.4"] = (false, HashAlgorithmName.SHA512),
    };
}
