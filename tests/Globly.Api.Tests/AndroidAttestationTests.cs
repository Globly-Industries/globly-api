using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Globly.Api.Posts;
using Xunit;

namespace Globly.Api.Tests;

/// <summary>Uses fake chains signed by a test root in place of Google's.</summary>
public class AndroidAttestationTests
{
    private const string Package = "com.globly.globly";
    private static readonly byte[] AppSigner = SHA256.HashData("release key"u8);
    private static readonly byte[] OtherSigner = SHA256.HashData("someone else"u8);

    private static readonly AttestationOptions Options = new()
    {
        PackageName = Package,
        SigningCertSha256 = [string.Join(":", Convert.ToHexString(AppSigner).Chunk(2).Select(c => new string(c)))],
    };

    private static readonly byte[] TheChallenge =
        AndroidAttestation.Challenge(Guid.Parse("0195f3a0-0000-7000-8000-000000000001"), new string('a', 64), 56.9496, 24.1052);

    private record Device(
        byte[]? Challenge = null,
        int SecurityLevel = 1,
        int Origin = 0,
        string PackageName = Package,
        byte[]? Signer = null,
        bool Locked = true,
        int BootState = 0);

    private static readonly ECDsa RootKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
    private static readonly X509Certificate2 Root = SelfSigned(RootKey, "CN=Test Attestation Root");
    private static readonly string RootKeyHash =
        Convert.ToHexStringLower(SHA256.HashData(Root.PublicKey.ExportSubjectPublicKeyInfo()));

    private static X509Certificate2 SelfSigned(ECDsa key, string name)
    {
        var req = new CertificateRequest(name, key, HashAlgorithmName.SHA384);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(10));
    }

    private static int _serial = 1;

    private static X509Certificate2 Issue(X509Certificate2 issuer, ECDsa issuerKey, ECDsa key, string name, X509Extension? extra = null)
    {
        var req = new CertificateRequest(name, key, HashAlgorithmName.SHA256);
        if (extra is null) req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        else req.CertificateExtensions.Add(extra);
        var serial = BitConverter.GetBytes(Interlocked.Increment(ref _serial));
        return req.Create(issuer.SubjectName, X509SignatureGenerator.CreateForECDsa(issuerKey),
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5), serial);
    }

    private static List<byte[]> Chain(Device d)
    {
        using var batchKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var batch = Issue(Root, RootKey, batchKey, "CN=Batch");
        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var leaf = Issue(batch, batchKey, leafKey, "CN=Android Keystore Key",
            new X509Extension("1.3.6.1.4.1.11129.2.1.17", KeyDescription(d), false));
        return [leaf.RawData, batch.RawData, Root.RawData];
    }

    private static byte[] KeyDescription(Device d)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            w.WriteInteger(300);
            w.WriteEnumeratedValue((Level)d.SecurityLevel);
            w.WriteInteger(300);
            w.WriteEnumeratedValue((Level)d.SecurityLevel);
            w.WriteOctetString(d.Challenge ?? TheChallenge);
            w.WriteOctetString([]);

            using (w.PushSequence()) // softwareEnforced
            {
                using (w.PushSequence(Tag(709)))
                    w.WriteOctetString(ApplicationId(d.PackageName, d.Signer ?? AppSigner));
            }
            using (w.PushSequence()) // hardwareEnforced
            {
                using (w.PushSequence(Tag(702))) w.WriteInteger(d.Origin);
                using (w.PushSequence(Tag(704)))
                using (w.PushSequence())
                {
                    w.WriteOctetString(new byte[32]);
                    w.WriteBoolean(d.Locked);
                    w.WriteEnumeratedValue((Boot)d.BootState);
                    w.WriteOctetString(new byte[32]);
                }
            }
        }
        return w.Encode();
    }

    private enum Level { Software, Tee, StrongBox }

    private enum Boot { Verified, SelfSigned, Unverified, Failed }

    private static Asn1Tag Tag(int number) => new(TagClass.ContextSpecific, number, isConstructed: true);

    private static byte[] ApplicationId(string package, byte[] signer)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            using (w.PushSetOf())
            using (w.PushSequence())
            {
                w.WriteOctetString(Encoding.UTF8.GetBytes(package));
                w.WriteInteger(42);
            }
            using (w.PushSetOf()) w.WriteOctetString(signer);
        }
        return w.Encode();
    }

    private static AttestationProblem Verify(List<byte[]> chain, AttestationOptions? options = null, Func<string, bool>? revoked = null) =>
        AndroidAttestation.Verify(chain, TheChallenge, options ?? Options, [RootKeyHash], revoked ?? (_ => false));

    [Fact]
    public void Genuine_capture_passes() => Assert.Equal(AttestationProblem.None, Verify(Chain(new Device())));

    [Fact]
    public void Strongbox_passes() => Assert.Equal(AttestationProblem.None, Verify(Chain(new Device(SecurityLevel: 2))));

    [Fact]
    public void Other_capture_file_or_location_fails()
    {
        var other = AndroidAttestation.Challenge(Guid.Parse("0195f3a0-0000-7000-8000-000000000001"), new string('a', 64), 56.9497, 24.1052);
        Assert.Equal(AttestationProblem.WrongChallenge, Verify(Chain(new Device(Challenge: other))));
    }

    [Fact]
    public void Software_key_fails() => Assert.Equal(AttestationProblem.SoftwareOnly, Verify(Chain(new Device(SecurityLevel: 0))));

    [Fact]
    public void Imported_key_fails() => Assert.Equal(AttestationProblem.ImportedKey, Verify(Chain(new Device(Origin: 2))));

    [Fact]
    public void Other_app_fails() => Assert.Equal(AttestationProblem.WrongApp, Verify(Chain(new Device(PackageName: "com.evil.app"))));

    [Fact]
    public void App_signed_with_another_key_fails() =>
        Assert.Equal(AttestationProblem.WrongApp, Verify(Chain(new Device(Signer: OtherSigner))));

    [Fact]
    public void Unlocked_or_modified_phone_fails()
    {
        Assert.Equal(AttestationProblem.UnlockedDevice, Verify(Chain(new Device(Locked: false))));
        Assert.Equal(AttestationProblem.UnlockedDevice, Verify(Chain(new Device(BootState: 2))));
    }

    [Fact]
    public void Unlocked_phone_passes_when_allowed()
    {
        var lax = new AttestationOptions { PackageName = Package, SigningCertSha256 = Options.SigningCertSha256, RequireLockedDevice = false };
        Assert.Equal(AttestationProblem.None, Verify(Chain(new Device(Locked: false, BootState: 2)), lax));
    }

    [Fact]
    public void Chain_to_another_root_fails()
    {
        using var fakeRootKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var fakeRoot = SelfSigned(fakeRootKey, "CN=Test Attestation Root");
        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var leaf = Issue(fakeRoot, fakeRootKey, leafKey, "CN=Key",
            new X509Extension("1.3.6.1.4.1.11129.2.1.17", KeyDescription(new Device()), false));
        Assert.Equal(AttestationProblem.UntrustedChain, Verify([leaf.RawData, fakeRoot.RawData]));
    }

    [Fact]
    public void Chain_with_a_swapped_link_fails()
    {
        var chain = Chain(new Device());
        var other = Chain(new Device());
        Assert.Equal(AttestationProblem.UntrustedChain, Verify([chain[0], other[1], chain[2]]));
    }

    [Fact]
    public void Revoked_certificate_fails()
    {
        var chain = Chain(new Device());
        using var batch = X509CertificateLoader.LoadCertificate(chain[1]);
        var serial = AndroidAttestation.SerialHex(batch);
        Assert.Equal(AttestationProblem.Revoked, Verify(chain, revoked: s => s == serial));
    }

    [Fact]
    public void Without_a_configured_signer_nothing_passes() =>
        Assert.Equal(AttestationProblem.NotConfigured, Verify(Chain(new Device()), new AttestationOptions()));

    [Fact]
    public void Garbage_is_malformed()
    {
        Assert.Equal(AttestationProblem.Malformed, Verify([[1, 2, 3], [4, 5, 6]]));
        Assert.Equal(AttestationProblem.Malformed, Verify([Root.RawData]));
    }

    // Same vector as the app's capture_attestation_test.dart.
    [Fact]
    public void Challenge_matches_the_app() =>
        Assert.Equal("b4d279ca0374e8fb252be9fa2899e31b4191c5cc649b18ce46399282926915b6",
            Convert.ToHexStringLower(AndroidAttestation.Challenge(
                Guid.Parse("0195F3A0-0000-7000-8000-000000000001"), new string('a', 64), 56.9496, -24.10525)));

    [Fact]
    public void Challenge_rounds_location_like_the_app()
    {
        var id = Guid.NewGuid();
        var hash = new string('b', 64);
        Assert.Equal(AndroidAttestation.Challenge(id, hash, 56.94960049, 24.1052),
            AndroidAttestation.Challenge(id, hash, 56.9496, 24.1052));
        Assert.NotEqual(AndroidAttestation.Challenge(id, hash, 56.949601, 24.1052),
            AndroidAttestation.Challenge(id, hash, 56.9496, 24.1052));
    }
}
