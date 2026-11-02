using DubUrl.ProviderTesting;
using NUnit.Framework;

namespace DubUrl.Tools.Testing;

public class CliQaCoverageTests
{
    [TestCase(typeof(CliPasswordAuthenticationQaContract))]
    [TestCase(typeof(CliOAuthQaContract))]
    [TestCase(typeof(PasswordAuthenticationQaContract))]
    [TestCase(typeof(TokenAuthenticationQaContract))]
    [TestCase(typeof(ClientCertificateAuthenticationQaContract))]
    [TestCase(typeof(OperatingSystemIdentityQaContract))]
    public void CliFixtureRetainsConsumerAndNativeAuthenticationCoverage(Type contract)
        => Assert.That(contract.IsAssignableFrom(typeof(CliConnectionCheckContract)), Is.True);
}
