using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HotRod.Client.Tests;

/// <summary>
/// Verifies how <see cref="TlsOptions"/> maps a consumer-supplied client certificate (and/or
/// collection) into the single collection handed to the TLS client-authentication path for
/// mutual TLS: null when nothing is set, and a merge of the single cert plus the collection
/// otherwise. Certificates are generated in-memory so no key material touches disk.
/// </summary>
public class TlsClientCertTests
{
    private static X509Certificate2 SelfSigned(string commonName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public void No_certificate_yields_a_null_collection()
    {
        var options = new TlsOptions();

        Assert.Null(options.BuildClientCertificates());
    }

    [Fact]
    public void A_single_certificate_is_offered()
    {
        using X509Certificate2 cert = SelfSigned("client");
        var options = new TlsOptions { ClientCertificate = cert };

        X509CertificateCollection? collection = options.BuildClientCertificates();

        Assert.NotNull(collection);
        Assert.Single(collection!.Cast<X509Certificate>());
        Assert.Equal(cert, collection![0]);
    }

    [Fact]
    public void A_supplied_collection_is_offered()
    {
        using X509Certificate2 first = SelfSigned("first");
        using X509Certificate2 second = SelfSigned("second");
        var options = new TlsOptions
        {
            ClientCertificates = new X509CertificateCollection { first, second },
        };

        X509CertificateCollection? collection = options.BuildClientCertificates();

        Assert.NotNull(collection);
        Assert.Equal(2, collection!.Count);
    }

    [Fact]
    public void The_single_certificate_and_collection_are_merged_single_first()
    {
        using X509Certificate2 primary = SelfSigned("primary");
        using X509Certificate2 chain = SelfSigned("chain");
        var options = new TlsOptions
        {
            ClientCertificate = primary,
            ClientCertificates = new X509CertificateCollection { chain },
        };

        X509CertificateCollection? collection = options.BuildClientCertificates();

        Assert.NotNull(collection);
        Assert.Equal(2, collection!.Count);
        Assert.Equal(primary, collection[0]);
        Assert.Equal(chain, collection[1]);
    }

    [Fact]
    public void Building_does_not_mutate_the_supplied_collection()
    {
        using X509Certificate2 primary = SelfSigned("primary");
        using X509Certificate2 chain = SelfSigned("chain");
        var supplied = new X509CertificateCollection { chain };
        var options = new TlsOptions { ClientCertificate = primary, ClientCertificates = supplied };

        options.BuildClientCertificates();

        Assert.Single(supplied.Cast<X509Certificate>());
    }
}
