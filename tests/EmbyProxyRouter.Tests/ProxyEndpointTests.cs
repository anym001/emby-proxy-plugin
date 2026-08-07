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

        // --- Parse errors must not carry the password --------------------------------------------

        /// <summary>
        /// Regression: a rejected address used to be quoted back verbatim, password and all.
        /// </summary>
        /// <remarks>
        /// A parse error is not confined to the settings page. It becomes
        /// <c>ProxySettings.ConfigError</c>, which ProxyRuntime writes to the Emby log on every
        /// save, <c>ProxyState.Explain</c> embeds in every fail-closed block the gate reports, and
        /// the health checker writes on every status change — and an Emby log is routinely pasted
        /// into an issue. <c>http://user:p@ss@proxy:8080</c> does not parse (the <c>@</c> in the
        /// password sees to that), so it took the message that echoed the whole address.
        ///
        /// Both renderings are checked: the log takes the English one, the settings page the
        /// localized one, and a password leaks just as badly through either.
        /// </remarks>
        [Theory]
        [InlineData("http://user:hunter2@x@proxy.example.com:8080")] // unparseable: @ in the password
        [InlineData("http://user:hunter2@ho st:8080")]               // unparseable: space in the host
        [InlineData("http://user:hunter2@[bad]:8080")]               // unparseable: bad IPv6 literal
        [InlineData("http://user:hunter2@proxy.example.com:99999")]  // unparseable: port out of range
        [InlineData("https://a:hunter2@c d:443")]
        [InlineData("user:hunter2@proxy.example.com:8080")]          // credentials without a scheme
        [InlineData("hunter2isthepassword@proxy.example.com")]
        public void AParseErrorNeverQuotesThePassword(string address)
        {
            ProxyEndpoint endpoint;
            LocalizedText error;
            var ok = ProxyEndpoint.TryParse(
                address, ProxyScheme.Http, null, null, out endpoint, out error);

            Assert.False(ok);
            Assert.NotNull(error);

            foreach (var rendered in new[] { error.Invariant(), error.Localized() })
            {
                Assert.False(string.IsNullOrEmpty(rendered));
                Assert.DoesNotContain("hunter2", rendered);
            }
        }

        /// <summary>
        /// The masking keeps the username, which is often the thing that is actually wrong.
        /// </summary>
        [Fact]
        public void MaskingKeepsTheUsernameAndTheHost()
        {
            ProxyEndpoint endpoint;
            LocalizedText error;
            Assert.False(ProxyEndpoint.TryParse(
                "http://alice:hunter2@ho st:8080", ProxyScheme.Http, null, null,
                out endpoint, out error));

            var rendered = error.Invariant();
            Assert.Contains("alice", rendered);
            Assert.Contains("ho st", rendered);
            Assert.DoesNotContain("hunter2", rendered);
        }

        /// <summary>
        /// A username with no password has nothing to hide, so it is left alone.
        /// </summary>
        [Fact]
        public void AUserNameWithoutAPasswordIsNotMasked()
        {
            ProxyEndpoint endpoint;
            LocalizedText error;
            Assert.False(ProxyEndpoint.TryParse(
                "http://alice@ho st:8080", ProxyScheme.Http, null, null, out endpoint, out error));

            Assert.Contains("alice@", error.Invariant());
        }

        /// <summary>
        /// Regression: credentials without a scheme fed the password to the port parser.
        /// </summary>
        /// <remarks>
        /// <c>alice:hunter2@proxy.example.com</c> has no <c>://</c>, so it took the host:port branch,
        /// where the port is everything after the last colon — <c>hunter2@proxy.example.com</c>. That
        /// is not a number, and the resulting "proxy port is not a number: …" quoted the password
        /// into the log. There is no authority to anchor userinfo to without a scheme, so it is
        /// rejected up front now, with a message that says what to write instead and quotes nothing.
        /// </remarks>
        [Theory]
        [InlineData("alice:hunter2@proxy.example.com")]
        [InlineData("alice:hunter2@proxy.example.com:1080")]
        [InlineData("alice@proxy.example.com:1080")]
        public void CredentialsWithoutASchemeAreRejected(string address)
        {
            ProxyEndpoint endpoint;
            LocalizedText error;
            Assert.False(ProxyEndpoint.TryParse(
                address, ProxyScheme.Http, null, null, out endpoint, out error));

            Assert.Equal("ErrUserInfoNeedsScheme", error.Key);
        }

        // --- The parsed Uri has to be the address that was entered --------------------------------

        /// <summary>
        /// Regression: a host containing a path separator silently swallowed the port.
        /// </summary>
        /// <remarks>
        /// UriBuilder validates a host far less than it validates a scheme. It takes one containing
        /// a <c>/</c> verbatim into the authority, and the <c>Uri</c> getter then parses the result
        /// back with the port inside the path — so <c>proxy.example.com/x:8080</c> came out as
        /// <c>proxy.example.com</c> on **port 80**, without throwing and without a word anywhere.
        /// Under fail-open that means every request goes out directly the moment port 80 refuses.
        /// </remarks>
        [Theory]
        [InlineData("proxy.example.com/path:8080")]
        [InlineData("proxy.example.com/x:8080")]
        [InlineData("host/../other:9090")]
        public void AnAddressWhoseHostSwallowsThePortIsRejected(string address)
        {
            ProxyEndpoint endpoint;
            Assert.False(TryParse(address, ProxyScheme.Http, out endpoint));
            Assert.Null(endpoint);
        }

        /// <summary>
        /// Whatever comes back, the port is the one that was asked for.
        /// </summary>
        /// <remarks>
        /// The general form of the case above: a successful parse may not quietly move the endpoint.
        /// Asserting the invariant rather than the three inputs that broke it means a future
        /// separator nobody has thought of is caught by this test too.
        /// </remarks>
        [Theory]
        [InlineData("proxy.example.com:8080", "proxy.example.com", 8080)]
        [InlineData("http://proxy.example.com:3128", "proxy.example.com", 3128)]
        [InlineData("https://proxy.example.com:443", "proxy.example.com", 443)]
        [InlineData("socks5://proxy.example.com:1080", "proxy.example.com", 1080)]
        [InlineData("PROXY.Example.COM:8080", "proxy.example.com", 8080)]
        [InlineData("http://[2001:db8::1]:8080", "[2001:db8::1]", 8080)]
        [InlineData("proxy.example.com.:8080", "proxy.example.com.", 8080)]
        public void AnAcceptedAddressKeepsItsHostAndPort(string address, string host, int port)
        {
            ProxyEndpoint endpoint;
            Assert.True(TryParse(address, ProxyScheme.Http, out endpoint));

            Assert.Equal(host, endpoint.Host);
            Assert.Equal(port, endpoint.Port);
            Assert.Equal(host, endpoint.Uri.Host);
            Assert.Equal(port, endpoint.Uri.Port);
        }

        /// <summary>
        /// Userinfo is parsed the way URL syntax says, and the endpoint says which host won.
        /// </summary>
        /// <remarks>
        /// <c>http://proxy.example.com:8080@evil.com:9999</c> is a valid URL whose host is
        /// <c>evil.com</c> — everything before the last <c>@</c> of the authority is credentials.
        /// .NET and every browser read it that way, so the plugin does too rather than inventing a
        /// heuristic. What it must not do is hide the outcome: <c>Describe()</c> is what the log and
        /// the status line both show, and it has to name the host that will actually be dialled.
        /// </remarks>
        [Fact]
        public void UserInfoConfusionIsResolvedByUrlSyntaxAndDescribedHonestly()
        {
            ProxyEndpoint endpoint;
            LocalizedText error;
            Assert.True(ProxyEndpoint.TryParse(
                "http://proxy.example.com:8080@evil.example:9999",
                ProxyScheme.Http, null, null, out endpoint, out error));

            Assert.Equal("evil.example", endpoint.Host);
            Assert.Equal(9999, endpoint.Port);

            // Whoever reads the log or the settings page sees the host that is really used.
            Assert.Contains("evil.example:9999", endpoint.Describe().Invariant());
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
