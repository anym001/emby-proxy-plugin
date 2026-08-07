using EmbyProxyRouter.Proxy;
using Xunit;

namespace EmbyProxyRouter.Tests
{
    public class ProxySettingsTests
    {
        // --- Check interval ---------------------------------------------------------------------

        /// <summary>
        /// Regression: the upper bound the settings page advertises was not enforced anywhere.
        /// </summary>
        /// <remarks>
        /// <c>[MaxValue(3600)]</c> constrains the spinner on the page, and nothing else. Emby reads
        /// the options back out of a JSON file under /config/plugins/configurations/, which can be
        /// edited by hand and does not go through Validate — so a value above the ceiling was taken
        /// verbatim, and the page's claimed range was decoration.
        /// </remarks>
        [Theory]
        [InlineData(0, PluginOptions.MinCheckIntervalSeconds)]
        [InlineData(-30, PluginOptions.MinCheckIntervalSeconds)]
        [InlineData(9, PluginOptions.MinCheckIntervalSeconds)]
        [InlineData(10, 10)]
        [InlineData(60, 60)]
        [InlineData(3600, 3600)]
        [InlineData(3601, PluginOptions.MaxCheckIntervalSeconds)]
        [InlineData(86400, PluginOptions.MaxCheckIntervalSeconds)]
        [InlineData(int.MaxValue, PluginOptions.MaxCheckIntervalSeconds)]
        public void TheCheckIntervalIsClampedAtBothEnds(int configured, int expected)
        {
            var settings = ProxySettings.FromOptions(new PluginOptions
            {
                EnableProxy = true,
                ProxyAddress = "http://proxy.example.com:8080",
                HealthCheckIntervalSeconds = configured
            });

            Assert.Equal(expected, (int)settings.HealthCheckInterval.TotalSeconds);
        }

        [Fact]
        public void TheAdvertisedBoundsAreTheEnforcedOnes()
        {
            // The page, the validation message and the clamp all read these two constants. If that
            // ever stops being true, the UI starts promising something the code does not do.
            Assert.True(PluginOptions.MinCheckIntervalSeconds > 0);
            Assert.True(PluginOptions.MaxCheckIntervalSeconds > PluginOptions.MinCheckIntervalSeconds);
        }

        // --- Check URLs -------------------------------------------------------------------------

        /// <summary>
        /// HTTP first: it is the cheaper probe, and a proxy that refuses to forward at all should
        /// not cost a TLS handshake before the verdict is in.
        /// </summary>
        [Fact]
        public void BothCheckUrlsAreKeptWithHttpFirst()
        {
            var settings = ProxySettings.FromOptions(new PluginOptions
            {
                EnableProxy = true,
                ProxyAddress = "http://proxy.example.com:8080",
                HealthCheckUrlHttp = "http://check.example.com/",
                HealthCheckUrlHttps = "https://check.example.com/"
            });

            Assert.Equal(2, settings.HealthCheckUrls.Count);
            Assert.StartsWith("http://", settings.HealthCheckUrls[0]);
            Assert.StartsWith("https://", settings.HealthCheckUrls[1]);
        }

        [Fact]
        public void AnEmptyCheckUrlMeansSkipRatherThanCheckNothing()
        {
            var settings = ProxySettings.FromOptions(new PluginOptions
            {
                EnableProxy = true,
                ProxyAddress = "http://proxy.example.com:8080",
                HealthCheckUrlHttp = "   ",
                HealthCheckUrlHttps = null
            });

            Assert.Empty(settings.HealthCheckUrls);
        }

        /// <summary>
        /// Regression: the scheme the two fields promise was enforced only by the settings page.
        /// </summary>
        /// <remarks>
        /// Same hole as the interval ceiling above, and a worse one. The fields are split by scheme
        /// so that one HTTP and one HTTPS probe together prove the proxy both forwards *and*
        /// tunnels; a proxy can serve one and refuse the other. <c>Validate</c> rejected a mismatched
        /// scheme on the page, but the options JSON is read back without going through it — so an
        /// https:// URL hand-edited into the HTTP field probed the CONNECT tunnel twice and reported
        /// a healthy proxy whose forwarding path had never been exercised.
        /// </remarks>
        [Fact]
        public void ACheckUrlWhoseSchemeContradictsItsFieldIsDropped()
        {
            var settings = ProxySettings.FromOptions(new PluginOptions
            {
                EnableProxy = true,
                ProxyAddress = "http://proxy.example.com:8080",
                HealthCheckUrlHttp = "https://check.example.com/",   // wrong field
                HealthCheckUrlHttps = "https://check.example.com/"
            });

            // The surviving entry is the HTTPS one; the plain-HTTP path is now known to be
            // unchecked rather than believed to be covered.
            Assert.Single(settings.HealthCheckUrls);
            Assert.StartsWith("https://", settings.HealthCheckUrls[0]);

            // And the drop is reported, because the plugin does not fail silently.
            Assert.Single(settings.ConfigWarnings);
            Assert.Contains("https://check.example.com/", settings.ConfigWarnings[0]);
        }

        [Theory]
        [InlineData("not a url")]
        [InlineData("/relative/only")]
        [InlineData("ftp://check.example.com/")]
        [InlineData("file:///etc/passwd")]
        public void AnUnusableCheckUrlIsDroppedAndReported(string url)
        {
            var settings = ProxySettings.FromOptions(new PluginOptions
            {
                EnableProxy = true,
                ProxyAddress = "http://proxy.example.com:8080",
                HealthCheckUrlHttp = url,
                HealthCheckUrlHttps = null
            });

            Assert.Empty(settings.HealthCheckUrls);
            Assert.Single(settings.ConfigWarnings);
        }

        [Fact]
        public void AValidConfigurationProducesNoWarnings()
        {
            var settings = ProxySettings.FromOptions(new PluginOptions
            {
                EnableProxy = true,
                ProxyAddress = "http://proxy.example.com:8080",
                HealthCheckUrlHttp = "http://check.example.com/",
                HealthCheckUrlHttps = "https://check.example.com/"
            });

            Assert.Equal(2, settings.HealthCheckUrls.Count);
            Assert.Empty(settings.ConfigWarnings);
        }

        /// <summary>
        /// An empty field is a deliberate "skip this probe", not something to warn about.
        /// </summary>
        [Fact]
        public void SkippingAProbeIsNotAWarning()
        {
            var settings = ProxySettings.FromOptions(new PluginOptions
            {
                EnableProxy = true,
                ProxyAddress = "http://proxy.example.com:8080",
                HealthCheckUrlHttp = "   ",
                HealthCheckUrlHttps = null
            });

            Assert.Empty(settings.HealthCheckUrls);
            Assert.Empty(settings.ConfigWarnings);
        }

        [Fact]
        public void TheDisabledSnapshotCarriesAnEmptyWarningList()
        {
            // Read on the routing path without a null check, so it must never be null.
            Assert.Empty(ProxySettings.Disabled().ConfigWarnings);
            Assert.Empty(ProxySettings.FromOptions(null).ConfigWarnings);
        }

        // --- Endpoint and errors ----------------------------------------------------------------

        [Fact]
        public void AParseFailureIsReportedOnlyWhileTheProxyIsEnabled()
        {
            var enabled = ProxySettings.FromOptions(new PluginOptions
            {
                EnableProxy = true,
                ProxyAddress = "nonsense"
            });

            Assert.Null(enabled.Endpoint);
            Assert.NotNull(enabled.ConfigError);

            // With the plugin switched off, an unfinished address is not an error to shout about.
            var disabled = ProxySettings.FromOptions(new PluginOptions
            {
                EnableProxy = false,
                ProxyAddress = "nonsense"
            });

            Assert.Null(disabled.Endpoint);
            Assert.Null(disabled.ConfigError);
        }

        [Fact]
        public void NullOptionsProduceTheDisabledSnapshot()
        {
            var settings = ProxySettings.FromOptions(null);

            Assert.False(settings.Enabled);
            Assert.Null(settings.Endpoint);
            Assert.NotNull(settings.Bypass);
            Assert.Empty(settings.HealthCheckUrls);
        }

        /// <summary>
        /// The disabled snapshot still carries the compiled-in bypass rules, so a caller that reads
        /// it before any configuration lands cannot conclude that nothing is bypassed.
        /// </summary>
        [Fact]
        public void TheDisabledSnapshotStillCarriesTheBypassRules()
        {
            var settings = ProxySettings.Disabled();

            Assert.False(settings.Enabled);
            Assert.True(settings.Bypass.IsBypassed(new System.Uri("http://192.168.1.1/")));
        }

        [Fact]
        public void TheFailurePolicyAndCertificateFlagCarryThrough()
        {
            var settings = ProxySettings.FromOptions(new PluginOptions
            {
                EnableProxy = true,
                ProxyAddress = "https://proxy.example.com:8443",
                AllowDirectWhenProxyUnavailable = true,
                IgnoreCertificateValidation = true
            });

            Assert.True(settings.FailOpen);
            Assert.True(settings.IgnoreCertificateValidation);
        }
    }
}
