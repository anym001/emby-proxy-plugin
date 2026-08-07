using System;
using System.Linq;
using EmbyProxyRouter.Proxy;
using Xunit;

namespace EmbyProxyRouter.Tests
{
    public class BypassRulesTests
    {
        private static bool IsBypassed(string list, string url)
        {
            return BypassRules.Parse(list, true).IsBypassed(new Uri(url));
        }

        /// <summary>With the private-networks switch off, which is not the default.</summary>
        private static bool IsBypassedWithoutPrivate(string list, string url)
        {
            return BypassRules.Parse(list, false).IsBypassed(new Uri(url));
        }

        // --- The compiled-in entries -----------------------------------------------------------

        /// <summary>
        /// These hold with an empty list, which is the point: the README promises them and a
        /// promise left to an editable default is a suggestion.
        /// </summary>
        [Theory]
        [InlineData("http://10.1.2.3/")]
        [InlineData("http://172.16.0.1/")]
        [InlineData("http://172.31.255.254/")]
        [InlineData("http://192.168.1.10/")]
        [InlineData("http://127.0.0.1/")]
        [InlineData("http://169.254.1.1/")]
        [InlineData("http://[::1]/")]
        [InlineData("http://[fc00::1]/")]
        [InlineData("http://[fe80::1]/")]
        [InlineData("http://localhost/")]
        [InlineData("http://anything.local/")]
        public void PrivateDestinationsAreAlwaysBypassed(string url)
        {
            Assert.True(IsBypassed(null, url));
        }

        /// <summary>
        /// The counterpart: ordinary metadata destinations must NOT be bypassed, or the plugin would
        /// route nothing while appearing to work.
        /// </summary>
        [Theory]
        [InlineData("https://api.themoviedb.org/")]
        [InlineData("https://image.tmdb.org/")]
        [InlineData("http://172.32.0.1/")]     // just outside 172.16.0.0/12
        [InlineData("http://172.15.255.255/")] // just below it
        [InlineData("http://11.0.0.1/")]       // just outside 10.0.0.0/8
        [InlineData("http://[2001:db8::1]/")]
        public void PublicDestinationsAreNotBypassed(string url)
        {
            Assert.False(IsBypassed(null, url));
        }

        /// <summary>
        /// Emby's licensing and Connect hosts go through the proxy like everything else.
        /// </summary>
        /// <remarks>
        /// They were compiled-in bypass entries once. The privacy argument for that was thin — a
        /// licence check carries the key identifying the installation either way — while the cost
        /// was real: a server whose only route outward is the proxy could not reach them at all.
        /// The accepted consequence, stated in README.md, is that a dead proxy under fail-closed now
        /// also stops Premiere from validating.
        ///
        /// Asserted explicitly rather than left to PublicDestinationsAreNotBypassed, so that
        /// re-adding them has to delete a test that says why they are gone.
        /// </remarks>
        [Theory]
        [InlineData("https://mb3admin.com/")]
        [InlineData("https://www.mb3admin.com/")]
        [InlineData("https://connect.emby.media/")]
        public void EmbysOwnHostsAreNotBypassed(string url)
        {
            Assert.False(IsBypassed(null, url));
        }

        // --- Single-label hostnames --------------------------------------------------------------

        /// <summary>
        /// A hostname with no dot cannot be a public DNS name, so it is always bypassed.
        /// </summary>
        /// <remarks>
        /// It resolves through the hosts file, the search domain or mDNS/NetBIOS — none of which a
        /// remote proxy can do. Proxying "nas" cannot succeed, and under fail-closed it costs the
        /// server every local service it reaches by short name. This is the one thing .NET's
        /// WebProxy(bypassOnLocal: true) covers that a CIDR list cannot express.
        /// </remarks>
        [Theory]
        [InlineData("http://nas:8096/")]
        [InlineData("http://router/")]
        [InlineData("http://emby/")]
        [InlineData("http://nas./")]        // trailing dot is the same host
        public void ASingleLabelHostnameIsBypassed(string url)
        {
            Assert.True(IsBypassed(null, url));
        }

        [Theory]
        [InlineData("http://nas.example.com/")]
        [InlineData("https://api.themoviedb.org/")]
        public void ADottedHostnameIsNotCoveredByTheSingleLabelRule(string url)
        {
            Assert.False(IsBypassed(null, url));
        }

        /// <summary>
        /// A trailing dot only makes an FQDN explicit; it must not change the route.
        /// </summary>
        [Theory]
        [InlineData(null, "http://anything.local./")]
        [InlineData("intranet.example.com", "https://intranet.example.com./")]
        [InlineData("*.example.com", "https://sub.example.com./")]
        public void ATrailingDotDoesNotChangeTheVerdict(string list, string url)
        {
            Assert.True(IsBypassed(list, url));
        }

        [Fact]
        public void ATrailingDotDoesNotTurnAPublicHostIntoABypassedOne()
        {
            Assert.False(IsBypassed(null, "https://api.themoviedb.org./"));
        }

        // --- The private-networks switch ---------------------------------------------------------

        /// <summary>
        /// Switched off, the private ranges go through the proxy like anything else.
        /// </summary>
        /// <remarks>
        /// The option exists for a host whose only route outward is a tunnel, where "everything,
        /// without exception" is the whole point and there is no direct path for this traffic to
        /// take anyway. The cost is real and documented: under fail-closed an unreachable proxy now
        /// takes the server's own LAN with it.
        /// </remarks>
        [Theory]
        [InlineData("http://10.1.2.3/")]
        [InlineData("http://192.168.1.10/")]
        [InlineData("http://172.16.0.1/")]
        [InlineData("http://169.254.1.1/")]
        [InlineData("http://[fc00::1]/")]
        [InlineData("http://[fe80::1]/")]
        [InlineData("http://anything.local/")]
        [InlineData("http://nas:8096/")]        // the single-label rule follows the same switch
        [InlineData("http://[::ffff:10.0.0.1]/")]
        public void SwitchingOffPrivateNetworksSendsThemThroughTheProxy(string url)
        {
            Assert.True(IsBypassed(null, url));                    // default
            Assert.False(IsBypassedWithoutPrivate(null, url));     // switched off
        }

        /// <summary>
        /// Loopback is never switchable, because the "on" position would always be wrong.
        /// </summary>
        /// <remarks>
        /// A proxy elsewhere has no route back to this machine, so a request to 127.0.0.1 sent
        /// through it cannot succeed under any configuration. .NET's own WebProxy takes the same
        /// view: IsBypassed returns true for a loopback host before it consults BypassProxyOnLocal
        /// or the bypass list at all.
        /// </remarks>
        [Theory]
        [InlineData("http://127.0.0.1/")]
        [InlineData("http://127.0.0.53:8096/")]
        [InlineData("http://[::1]/")]
        [InlineData("http://localhost/")]
        [InlineData("http://[::ffff:127.0.0.1]/")]
        public void LoopbackIsBypassedEvenWithPrivateNetworksSwitchedOff(string url)
        {
            Assert.True(IsBypassedWithoutPrivate(null, url));
        }

        /// <summary>
        /// The user's own list still applies with the switch off — that is what makes it a usable
        /// escape hatch rather than a second copy of the same policy.
        /// </summary>
        [Fact]
        public void TheUserListStillAppliesWithPrivateNetworksSwitchedOff()
        {
            Assert.True(IsBypassedWithoutPrivate("192.168.0.0/16", "http://192.168.1.10/"));
            Assert.True(IsBypassedWithoutPrivate("nas.home.arpa", "http://nas.home.arpa/"));
            Assert.True(IsBypassedWithoutPrivate("mb3admin.com", "https://mb3admin.com/"));
        }

        [Fact]
        public void TheRuleSetReportsWhichPolicyItWasBuiltWith()
        {
            // ProxyRuntime logs this on every apply, so it has to be readable back off the rules.
            Assert.True(BypassRules.Parse(null, true).BypassPrivateNetworks);
            Assert.False(BypassRules.Parse(null, false).BypassPrivateNetworks);
        }

        // --- IPv4-mapped IPv6 ------------------------------------------------------------------

        /// <summary>
        /// Regression: "::ffff:10.0.0.1" is the same host as "10.0.0.1".
        /// </summary>
        /// <remarks>
        /// CidrRule compares the address family before the bytes, so the compiled-in IPv4 ranges
        /// never saw a mapped address. A LAN destination was therefore sent through the proxy — or
        /// blocked outright under fail-closed — purely because of how it had been spelled.
        /// </remarks>
        [Theory]
        [InlineData("http://[::ffff:10.0.0.1]/")]
        [InlineData("http://[::ffff:192.168.1.10]/")]
        [InlineData("http://[::ffff:127.0.0.1]/")]
        public void AnIpv4MappedIpv6AddressMatchesTheIpv4Ranges(string url)
        {
            Assert.True(IsBypassed(null, url));
        }

        [Fact]
        public void AMappedAddressOutsideThePrivateRangesIsStillNotBypassed()
        {
            Assert.False(IsBypassed(null, "http://[::ffff:8.8.8.8]/"));
        }

        /// <summary>
        /// A rule written in the mapped form keeps working: the unmapped pass runs first.
        /// </summary>
        [Fact]
        public void ARuleWrittenInMappedFormStillMatches()
        {
            Assert.True(IsBypassed("::ffff:203.0.113.5", "http://[::ffff:203.0.113.5]/"));
        }

        // --- User-supplied rules ---------------------------------------------------------------

        [Fact]
        public void AWildcardMatchesTheDomainAndItsSubdomains()
        {
            Assert.True(IsBypassed("*.example.com", "https://example.com/"));
            Assert.True(IsBypassed("*.example.com", "https://sub.example.com/"));
            Assert.True(IsBypassed("*.example.com", "https://deep.sub.example.com/"));
        }

        /// <summary>
        /// A wildcard must match on a label boundary, not on a string suffix — otherwise
        /// "*.example.com" would also cover "notexample.com", which someone else controls.
        /// </summary>
        [Fact]
        public void AWildcardDoesNotMatchAcrossALabelBoundary()
        {
            Assert.False(IsBypassed("*.example.com", "https://notexample.com/"));
            Assert.False(IsBypassed("*.example.com", "https://example.com.evil.net/"));
        }

        [Fact]
        public void AnExactHostMatchesOnlyItself()
        {
            Assert.True(IsBypassed("intranet.example.com", "https://intranet.example.com/"));
            Assert.False(IsBypassed("intranet.example.com", "https://other.example.com/"));
            Assert.False(IsBypassed("intranet.example.com", "https://sub.intranet.example.com/"));
        }

        [Fact]
        public void HostMatchingIsCaseInsensitive()
        {
            Assert.True(IsBypassed("Intranet.Example.COM", "https://intranet.example.com/"));
        }

        [Theory]
        [InlineData("203.0.113.0/24", "http://203.0.113.7/", true)]
        [InlineData("203.0.113.0/24", "http://203.0.114.7/", false)]
        [InlineData("203.0.113.64/26", "http://203.0.113.65/", true)]
        [InlineData("203.0.113.64/26", "http://203.0.113.63/", false)]
        [InlineData("2001:db8::/32", "http://[2001:db8::1]/", true)]
        [InlineData("2001:db8::/32", "http://[2001:db9::1]/", false)]
        public void CidrRulesMatchOnThePrefix(string rule, string url, bool expected)
        {
            Assert.Equal(expected, IsBypassed(rule, url));
        }

        [Fact]
        public void ASingleIpRuleMatchesThatAddressOnly()
        {
            Assert.True(IsBypassed("203.0.113.5", "http://203.0.113.5/"));
            Assert.False(IsBypassed("203.0.113.5", "http://203.0.113.6/"));
        }

        [Fact]
        public void EntriesMayBeSeparatedByNewlinesCommasOrSemicolons()
        {
            const string list = "a.example.com\nb.example.com,c.example.com;d.example.com";

            Assert.True(IsBypassed(list, "https://a.example.com/"));
            Assert.True(IsBypassed(list, "https://b.example.com/"));
            Assert.True(IsBypassed(list, "https://c.example.com/"));
            Assert.True(IsBypassed(list, "https://d.example.com/"));
        }

        [Fact]
        public void CommentsAndBlankLinesAreIgnored()
        {
            var rules = BypassRules.Parse("# a comment\n\n   \nreal.example.com\n", true);

            Assert.Empty(rules.Errors);
            Assert.True(rules.IsBypassed(new Uri("https://real.example.com/")));
        }

        // --- Errors ----------------------------------------------------------------------------

        [Theory]
        [InlineData("10.0.0.0/copper")]
        [InlineData("10.0.0.0/33")]
        [InlineData("999.0.0.1/8")]
        [InlineData("*.")]
        public void AMalformedEntryIsReportedRatherThanSwallowed(string entry)
        {
            var rules = BypassRules.Parse(entry, true);
            Assert.NotEmpty(rules.Errors);
        }

        /// <summary>
        /// A bad entry must not take the rest of the list — or the compiled-in entries — with it.
        /// </summary>
        [Fact]
        public void AMalformedEntryDoesNotDisableTheValidOnes()
        {
            var rules = BypassRules.Parse("10.0.0.0/copper\ngood.example.com", true);

            Assert.Single(rules.Errors);
            Assert.True(rules.IsBypassed(new Uri("https://good.example.com/")));
            Assert.True(rules.IsBypassed(new Uri("http://192.168.1.1/")));
        }

        [Fact]
        public void AValidListReportsNoErrors()
        {
            var rules = BypassRules.Parse("10.9.0.0/16\n*.example.com\nhost.example.net\n203.0.113.5", true);
            Assert.Empty(rules.Errors);
        }

        /// <summary>
        /// The compiled-in list is parsed on every call, so it had better parse cleanly — and it
        /// had better not have been emptied out, which no other test here would notice.
        /// </summary>
        [Fact]
        public void TheCompiledInListIsItselfValid()
        {
            Assert.Empty(BypassRules.Parse(null, true).Errors);
            Assert.Empty(BypassRules.Parse(null, false).Errors);

            var always = Split(BypassRules.Always);
            var private_ = Split(BypassRules.PrivateNetworks);

            // Always is loopback only - everything else has to be switchable, or the switch is a
            // half-truth. Splitting them wrongly is the mistake this catches.
            Assert.Equal(3, always.Length);
            Assert.Equal(7, private_.Length);

            // Emby's own hosts were removed from both deliberately; a merge that quietly brings
            // them back should fail here.
            foreach (var entry in always.Concat(private_))
            {
                Assert.DoesNotContain("emby", entry, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("mb3admin", entry, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string[] Split(string list)
        {
            return list.Split('\n').Where(l => l.Trim().Length > 0).ToArray();
        }

        [Fact]
        public void ANullDestinationIsNotBypassed()
        {
            Assert.False(BypassRules.Parse(null, true).IsBypassed(null));
        }
    }
}
