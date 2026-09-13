using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.Assist.Tests;

public class RedactionTests
{
    [Fact]
    public void AnAssignmentKeepsItsNameAndLosesItsValue()
    {
        var scrubbed = Redaction.Scrub("DB_PASSWORD=hunter2");

        scrubbed.Text.ShouldBe("DB_PASSWORD=[redacted]");
        scrubbed.Count.ShouldBe(1);
    }

    [Fact]
    public void WhichVariableIsUsuallyTheQuestionAndIsNotTheSecret()
    {
        var scrubbed = Redaction.Scrub("export STRIPE_SECRET_KEY=sk_live_51H8xQ2abcdefghijklmnop");

        scrubbed.Text.ShouldContain("STRIPE_SECRET_KEY");
        scrubbed.Text.ShouldNotContain("sk_live");
    }

    [Theory]
    [InlineData("API_KEY=abc123")]
    [InlineData("api_key: abc123")]
    [InlineData("\"apiKey\": \"abc123\"")]
    [InlineData("AWS_SECRET_ACCESS_KEY=abc123")]
    [InlineData("GITHUB_TOKEN=abc123")]
    [InlineData("db_password = abc123")]
    [InlineData("CLIENT_SECRET=abc123")]
    [InlineData("passphrase=abc123")]
    public void SecretShapedNamesAreRecognised(string line) =>
        Redaction.Scrub(line).Text.ShouldNotContain("abc123", Case.Sensitive);

    [Theory]
    [InlineData("PATH=/usr/bin:/bin")]
    [InlineData("LANG=en_GB.UTF-8")]
    [InlineData("count=42")]
    public void OrdinaryAssignmentsAreLeftAlone(string line)
    {
        var scrubbed = Redaction.Scrub(line);

        scrubbed.Text.ShouldBe(line);
        scrubbed.Count.ShouldBe(0);
    }

    [Fact]
    public void APrivateKeyBlockGoesWhole()
    {
        var session = string.Join("\n",
            "$ cat ~/.ssh/id_rsa",
            "-----BEGIN OPENSSH PRIVATE KEY-----",
            "b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAABlwAAAAdzc2gtcn",
            "NhAAAAAwEAAQAAAYEAy2Nq0Yl8bDpXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
            "-----END OPENSSH PRIVATE KEY-----",
            "$ ");

        var scrubbed = Redaction.Scrub(session);

        scrubbed.Text.ShouldNotContain("b3BlbnNzaC");
        scrubbed.Text.ShouldContain("[redacted private key]");
        // The command that produced it is still there, which is usually the
        // thing being asked about.
        scrubbed.Text.ShouldContain("cat ~/.ssh/id_rsa");
        scrubbed.Count.ShouldBe(1);
    }

    [Fact]
    public void ABearerHeaderGoes()
    {
        var scrubbed = Redaction.Scrub("curl -H 'Authorization: Bearer eyJhbGciOiJIUzI1NiJ9abcdef' https://api.example.com");

        scrubbed.Text.ShouldNotContain("eyJhbGciOiJIUzI1NiJ9abcdef");
        scrubbed.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void CredentialsInAConnectionStringGoAndTheUserStays()
    {
        var scrubbed = Redaction.Scrub("postgres://deploy:s3cr3tpassword@db-01.internal:5432/app");

        scrubbed.Text.ShouldContain("deploy");
        scrubbed.Text.ShouldNotContain("s3cr3tpassword");
        scrubbed.Text.ShouldContain("db-01.internal:5432/app");
    }

    [Fact]
    public void AJwtGoes()
    {
        var jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";

        Redaction.Scrub($"token is {jwt} apparently").Text.ShouldNotContain(jwt);
    }

    [Theory]
    [InlineData("sk-ant-api03-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789")]
    [InlineData("ghp_AbCdEfGhIjKlMnOpQrStUvWxYz0123")]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("xoxb-123456789012-1234567890123-AbCdEfGhIjKlMnOpQrSt")]
    [InlineData("glpat-AbCdEfGhIjKlMnOpQrSt")]
    public void TheRecognisableProviderFormatsGo(string key) =>
        Redaction.Scrub($"the key was {key} in the log").Text.ShouldNotContain(key);

    [Fact]
    public void ADockerLoginGoes()
    {
        var scrubbed = Redaction.Scrub("docker login -u deploy -p ThisIsThePassword registry.example.com");

        scrubbed.Text.ShouldNotContain("ThisIsThePassword");
        scrubbed.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void OneSecretIsCountedOnce()
    {
        // Both the assignment rule and the provider-key rule recognise this. The
        // order the rules run in is what keeps the count honest, and the count is
        // the only evidence the redactor ran.
        Redaction.Scrub("ANTHROPIC_API_KEY=sk-ant-api03-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789").Count.ShouldBe(1);
    }

    [Fact]
    public void ASessionWithNoSecretsIsUntouched()
    {
        var session = "deploy@web-01:~$ df -h\nFilesystem  Size  Used Avail Use% Mounted on\n/dev/sda1    49G   46G  1.2G  98% /";

        var scrubbed = Redaction.Scrub(session);

        scrubbed.Text.ShouldBe(session);
        scrubbed.Count.ShouldBe(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NothingIsNothing(string? text)
    {
        var scrubbed = Redaction.Scrub(text);

        scrubbed.Text.ShouldBeEmpty();
        scrubbed.Count.ShouldBe(0);
    }

    [Fact]
    public void SeveralSecretsAreAllCounted()
    {
        var scrubbed = Redaction.Scrub(string.Join("\n",
            "DB_PASSWORD=one",
            "API_TOKEN=two",
            "PATH=/usr/bin"));

        scrubbed.Count.ShouldBe(2);
        scrubbed.Text.ShouldContain("PATH=/usr/bin");
    }
}
