using EmbyProxyRouter.Localization;
using EmbyProxyRouter.Proxy;
using Xunit;

namespace EmbyProxyRouter.Tests
{
    public class ProxyEndpointTests
    {
        private static bool TryParse(string address, ProxyScheme scheme, out ProxyEndpoint endpoint)
        {
            LocalizedText error;
            return ProxyEndpoint.TryParse(address, scheme, null, null, out endpoint, out error);
        }

        // --- Explicit ports, including the ones a Uri normalises away --------------------------

        /// <summary>
        /// Regression: an explicitly written default port used to be rejected.
        /// </summary>
        /// <remarks>
        /// The old code asked the parsed <c>Uri</c> whether it had a port, but
        /// <c>new Uri("https://p:443/").IsDefaultPort</c> is true — indistinguishable from
        /// <c>https://p/</c>. So a user who wrote <c>https://proxy:443</c> was told to supply the
        /// port they had just supplied. The raw address text is the only place the distinction
        /// survives, which is what <c>AuthorityHasPort</c> now reads.
        /// </remarks>
        [Theory]
        [InlineData("https://proxy.example.com:443", 443)]
        [InlineData("http://proxy.example.com:80", 80)]
        [InlineData("socks5://proxy.example.com:1080", 1080)]
        [InlineData("http://proxy.example.com:3128", 3128)]
        public void ExplicitPortIsHonouredEvenWhenItIsTheDefault(string address, int expected)
        {
            ProxyEndpoint endpoint;
            Assert.True(TryParse(address, ProxyScheme.Http, out endpoint));
            Assert.Equal(expected, endpoint.Port);
        }

        [Theory]
        [InlineData("http://proxy.example.com")]
        [InlineData("https://proxy.example.com")]
        [InlineData("socks5://proxy.example.com")]
        [InlineData("http://proxy.example.com/")]
        public void AUrlWithoutAPortIsRejected(string address)
        {
            ProxyEndpoint endpoint;
            Assert.False(TryParse(address, ProxyScheme.Http, out endpoint));
        }

        [Fact]
        public void HostPortWithoutASchemeUsesTheFallbackScheme()
        {
            ProxyEndpoint endpoint;
            Assert.True(TryParse("192.168.1.10:1080", ProxyScheme.Socks5, out endpoint));

            Assert.Equal(ProxyScheme.Socks5, endpoint.Scheme);
            Assert.True(endpoint.IsSocks);
            Assert.Equal("socks5", endpoint.Uri.Scheme);
            Assert.Equal(1080, endpoint.Port);
        }

        [Fact]
        public void AnExplicitSchemeBeatsTheFallback()
        {
            ProxyEndpoint endpoint;
            Assert.True(TryParse("socks5://192.168.1.10:1080", ProxyScheme.Http, out endpoint));
            Assert.Equal(ProxyScheme.Socks5, endpoint.Scheme);
        }

        // --- Rejections that must be answers, not exceptions -----------------------------------

        /// <summary>
        /// Regression: a host UriBuilder refuses used to escape as an exception.
        /// </summary>
        /// <remarks>
        /// UriBuilder validates lazily — the constructor takes any string and the <c>Uri</c> getter
        /// is what throws. TryParse runs inside the settings page's Validate and inside
        /// OnOptionsSaved, neither of which has anywhere to put an exception, so it has to come back
        /// as false with a message.
        /// </remarks>
        [Theory]
        [InlineData("http://a b c:8080")]
        [InlineData("ho st:8080")]
        [InlineData("[not-an-ipv6:8080")]
        public void AnUnusableHostReturnsFalseRatherThanThrowing(string address)
        {
            ProxyEndpoint endpoint;
            LocalizedText error;
            var ok = ProxyEndpoint.TryParse(
                address, ProxyScheme.Http, null, null, out endpoint, out error);

            Assert.False(ok);
            Assert.Null(endpoint);
            Assert.NotNull(error);
            Assert.False(string.IsNullOrEmpty(error.Invariant()));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void AnEmptyAddressIsRejected(string address)
        {
            ProxyEndpoint endpoint;
            Assert.False(TryParse(address, ProxyScheme.Http, out endpoint));
        }

        [Theory]
        [InlineData("proxy.example.com")]         // no port at all
        [InlineData("proxy.example.com:")]        // trailing colon
        [InlineData("proxy.example.com:abc")]     // not a number
        [InlineData("proxy.example.com: 8080")]   // NumberStyles.None rejects the space
        [InlineData("proxy.example.com:-1")]      // and the sign
        [InlineData("proxy.example.com:0")]
        [InlineData("proxy.example.com:65536")]
        public void AnUnusablePortIsRejected(string address)
        {
            ProxyEndpoint endpoint;
            Assert.False(TryParse(address, ProxyScheme.Http, out endpoint));
        }

        [Fact]
        public void AnUnsupportedSchemeIsRejected()
        {
            ProxyEndpoint endpoint;
            Assert.False(TryParse("ftp://proxy.example.com:21", ProxyScheme.Http, out endpoint));
        }

        // --- Credentials -----------------------------------------------------------------------

        /// <summary>
        /// The URI handed to .NET must never carry userinfo.
        /// </summary>
        /// <remarks>
        /// .NET ignores userinfo in a SOCKS proxy URI outright and negotiates "no authentication"
        /// instead, so leaving it there produces a configuration that looks authenticated and
        /// silently is not. Credentials only work out-of-band, via IWebProxy.Credentials.
        /// </remarks>
        [Fact]
        public void UserInfoIsMovedOutOfTheUriIntoTheCredential()
        {
            ProxyEndpoint endpoint;
            LocalizedText error;
            Assert.True(ProxyEndpoint.TryParse(
                "socks5://alice:s3cret@proxy.example.com:1080",
                ProxyScheme.Http, null, null, out endpoint, out error));

            Assert.Equal(string.Empty, endpoint.Uri.UserInfo);
            Assert.NotNull(endpoint.Credential);
            Assert.Equal("alice", endpoint.Credential.UserName);
            Assert.Equal("s3cret", endpoint.Credential.Password);
        }

        [Fact]
        public void TheExplicitUsernameFieldBeatsUserInfoInTheUrl()
        {
            ProxyEndpoint endpoint;
            LocalizedText error;
            Assert.True(ProxyEndpoint.TryParse(
                "socks5://fromurl:fromurl@proxy.example.com:1080",
                ProxyScheme.Http, "fromfield", "fieldpass", out endpoint, out error));

            Assert.Equal("fromfield", endpoint.Credential.UserName);
            Assert.Equal("fieldpass", endpoint.Credential.Password);
        }

        [Fact]
        public void NoCredentialsAnywhereLeavesTheCredentialNull()
        {
            ProxyEndpoint endpoint;
            Assert.True(TryParse("proxy.example.com:8080", ProxyScheme.Http, out endpoint));
            Assert.Null(endpoint.Credential);
        }

        [Fact]
        public void UserInfoWithAPortIsStillParsedAsAPort()
        {
            ProxyEndpoint endpoint;
            LocalizedText error;
            Assert.True(ProxyEndpoint.TryParse(
                "http://alice:s3cret@proxy.example.com:8080",
                ProxyScheme.Http, null, null, out endpoint, out error));

            Assert.Equal(8080, endpoint.Port);
            Assert.Equal("proxy.example.com", endpoint.Host);
        }

        // --- IPv6 ------------------------------------------------------------------------------

        /// <summary>
        /// A bracketed IPv6 literal is full of colons, none of which introduces a port.
        /// </summary>
        [Fact]
        public void ABracketedIpv6LiteralWithAPortIsParsed()
        {
            ProxyEndpoint endpoint;
            Assert.True(TryParse("http://[2001:db8::1]:8080", ProxyScheme.Http, out endpoint));
            Assert.Equal(8080, endpoint.Port);
        }

        [Fact]
        public void ABracketedIpv6LiteralWithoutAPortIsRejected()
        {
            ProxyEndpoint endpoint;
            Assert.False(TryParse("http://[2001:db8::1]", ProxyScheme.Http, out endpoint));
        }

        // --- Describe --------------------------------------------------------------------------

        /// <summary>
        /// Describe() reaches the log and the dashboard, so it must not leak the password.
        /// </summary>
        [Fact]
        public void DescribeNamesTheUserButNeverThePassword()
        {
            ProxyEndpoint endpoint;
            LocalizedText error;
            Assert.True(ProxyEndpoint.TryParse(
                "socks5://proxy.example.com:1080",
                ProxyScheme.Http, "alice", "s3cret", out endpoint, out error));

            // Both renderings reach a reader: the log takes the English one, the settings page the
            // localized one. Neither may carry the password.
            foreach (var described in new[] { endpoint.Describe().Invariant(), endpoint.Describe().Localized() })
            {
                Assert.Contains("alice", described);
                Assert.Contains("proxy.example.com:1080", described);
                Assert.DoesNotContain("s3cret", described);
            }
        }
    }
}
