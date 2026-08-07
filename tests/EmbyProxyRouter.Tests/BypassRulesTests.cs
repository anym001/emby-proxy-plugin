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
            return BypassRules.Parse(list).IsBypassed(new Uri(url));
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
        [InlineData("https://mb3admin.com/")]
        [InlineData("https://www.mb3admin.com/")]
        [InlineData("https://connect.emby.media/")]
        public void PrivateAndLicensingDestinationsAreAlwaysBypassed(string url)
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
            var rules = BypassRules.Parse("# a comment\n\n   \nreal.example.com\n");

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
            var rules = BypassRules.Parse(entry);
            Assert.NotEmpty(rules.Errors);
        }

        /// <summary>
        /// A bad entry must not take the rest of the list — or the compiled-in entries — with it.
        /// </summary>
        [Fact]
        public void AMalformedEntryDoesNotDisableTheValidOnes()
        {
            var rules = BypassRules.Parse("10.0.0.0/copper\ngood.example.com");

            Assert.Single(rules.Errors);
            Assert.True(rules.IsBypassed(new Uri("https://good.example.com/")));
            Assert.True(rules.IsBypassed(new Uri("http://192.168.1.1/")));
        }

        [Fact]
        public void AValidListReportsNoErrors()
        {
            var rules = BypassRules.Parse("10.9.0.0/16\n*.example.com\nhost.example.net\n203.0.113.5");
            Assert.Empty(rules.Errors);
        }

        /// <summary>
        /// The compiled-in list is parsed on every call, so it had better parse cleanly — and it
        /// had better not have been emptied out, which no other test here would notice.
        /// </summary>
        [Fact]
        public void TheCompiledInListIsItselfValid()
        {
            Assert.Empty(BypassRules.Parse(null).Errors);

            var entries = BypassRules.Always
                .Split('\n')
                .Where(l => l.Trim().Length > 0)
                .ToArray();

            Assert.Equal(13, entries.Length);
        }

        [Fact]
        public void ANullDestinationIsNotBypassed()
        {
            Assert.False(BypassRules.Parse(null).IsBypassed(null));
        }
    }
}
