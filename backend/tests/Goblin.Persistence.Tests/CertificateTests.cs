using System;
using System.IO;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Goblin.Persistence.Tests;

public sealed class PostgresCertificateFactAttribute : FactAttribute
{
    public PostgresCertificateFactAttribute()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("GOBLIN_TEST_POSTGRES_APP"));
        if (connection.SslMode != SslMode.VerifyFull || string.IsNullOrEmpty(connection.SslCertificate))
            Skip = "Set GOBLIN_TEST_POSTGRES_APP to a VerifyFull client-certificate connection to run certificate tests.";
    }
}

public sealed class CertificateTests
{
    private static NpgsqlConnectionStringBuilder App() => new(Environment.GetEnvironmentVariable("GOBLIN_TEST_POSTGRES_APP"))
    {
        Pooling = false,
        Timeout = 5,
    };

    [PostgresCertificateFact]
    public async Task CertificateAuthenticatesTheApplicationWithoutAPassword()
    {
        NpgsqlConnectionStringBuilder settings = App();
        Assert.True(string.IsNullOrEmpty(settings.Password));
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT current_user, ssl, client_dn FROM pg_stat_ssl WHERE pid = pg_backend_pid()", connection);
        await using NpgsqlDataReader row = await command.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        Assert.Equal("goblin_app", row.GetString(0));
        Assert.True(row.GetBoolean(1));
        Assert.Contains("CN=goblin_app", row.GetString(2));
    }

    [PostgresCertificateFact]
    public async Task MissingClientCertificateIsRejectedEvenWithAPassword()
    {
        NpgsqlConnectionStringBuilder settings = App();
        settings.SslCertificate = null;
        settings.SslKey = null;
        settings.Password = "unused-test-password";
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        PostgresException error = await Assert.ThrowsAsync<PostgresException>(() => connection.OpenAsync());
        Assert.Equal(PostgresErrorCodes.InvalidAuthorizationSpecification, error.SqlState);
    }

    [PostgresCertificateFact]
    public async Task ApplicationCertificateCannotAuthenticateAsAdministrator()
    {
        NpgsqlConnectionStringBuilder settings = App();
        settings.Username = "goblin_admin";
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        PostgresException error = await Assert.ThrowsAsync<PostgresException>(() => connection.OpenAsync());
        Assert.Equal(PostgresErrorCodes.InvalidAuthorizationSpecification, error.SqlState);
    }

    [PostgresCertificateFact]
    public async Task UnencryptedConnectionsAreRejected()
    {
        NpgsqlConnectionStringBuilder settings = App();
        settings.SslMode = SslMode.Disable;
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        PostgresException error = await Assert.ThrowsAsync<PostgresException>(() => connection.OpenAsync());
        Assert.Equal(PostgresErrorCodes.InvalidAuthorizationSpecification, error.SqlState);
    }

    [PostgresCertificateFact]
    public async Task UntrustedServerCertificateIsRejected()
    {
        string directory = Path.Combine(Path.GetTempPath(), "goblin-untrusted-ca-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Unrelated test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            NpgsqlConnectionStringBuilder settings = App();
            settings.RootCertificate = Path.Combine(directory, "ca.crt");
            await File.WriteAllTextAsync(settings.RootCertificate, certificate.ExportCertificatePem());
            await using var connection = new NpgsqlConnection(settings.ConnectionString);
            NpgsqlException error = await Assert.ThrowsAsync<NpgsqlException>(() => connection.OpenAsync());
            Assert.IsType<AuthenticationException>(error.InnerException);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [PostgresCertificateFact]
    public async Task IncorrectServerHostnameIsRejected()
    {
        var builder = new NpgsqlDataSourceBuilder(App().ConnectionString);
        builder.UseSslClientAuthenticationOptionsCallback(options => options.TargetHost = "not-the-database.invalid");
        await using NpgsqlDataSource source = builder.Build();
        NpgsqlException error = await Assert.ThrowsAsync<NpgsqlException>(async () => { await using NpgsqlConnection connection = await source.OpenConnectionAsync(); });
        Assert.IsType<AuthenticationException>(error.InnerException);
    }
}
