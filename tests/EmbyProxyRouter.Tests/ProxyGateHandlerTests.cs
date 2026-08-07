using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbyProxyRouter.Proxy;
using Xunit;

namespace EmbyProxyRouter.Tests
{
    /// <summary>
    /// The gate is where fail-closed is actually enforced — an IWebProxy cannot refuse a request,
    /// it can only name a proxy or return null, and null means "connect directly".
    /// </summary>
    public class ProxyGateHandlerTests
    {
        private static ProxySettings Settings(
            bool enabled = true, string address = "socks5://proxy.example.com:1080",
            bool failOpen = false)
        {
            return ProxySettings.FromOptions(new PluginOptions
            {
                EnableProxy = enabled,
                ProxyAddress = address,
                AllowDirectWhenProxyUnavailable = failOpen
            });
        }

        private static ProxyState StateWith(ProxySettings settings, ProxyHealth health)
        {
            var state = new ProxyState();
            state.Apply(settings);

            if (health != ProxyHealth.Unknown)
            {
                state.SetHealth(health, "test");
            }

            return state;
        }

        private static async Task<Exception> SendAsync(HttpMessageInvoker invoker, string url)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                using (await invoker.SendAsync(request, CancellationToken.None).ConfigureAwait(false))
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        // --- Fail-closed ------------------------------------------------------------------------

        /// <summary>
        /// The request must not reach the inner handler at all. Anything else is a leak.
        /// </summary>
        [Fact]
        public async Task ABlockedRequestNeverReachesTheInnerHandler()
        {
            var settings = Settings(failOpen: false);
            var state = StateWith(settings, ProxyHealth.Unreachable);
            var inner = new StubHandler();
            var logger = new RecordingLogger();

            using (var invoker = new HttpMessageInvoker(
                       new ProxyGateHandler(inner, state, logger, null)))
            {
                var error = await SendAsync(invoker, "https://api.themoviedb.org/3/movie/1");

                Assert.IsType<HttpRequestException>(error);
                Assert.Equal(0, inner.Calls);
                Assert.Single(logger.Warnings);
            }
        }

        [Fact]
        public async Task AnUncheckedProxyBlocksToo()
        {
            var settings = Settings(failOpen: false);
            var state = StateWith(settings, ProxyHealth.Unknown);
            var inner = new StubHandler();

            using (var invoker = new HttpMessageInvoker(
                       new ProxyGateHandler(inner, state, new RecordingLogger(), null)))
            {
                Assert.NotNull(await SendAsync(invoker, "https://api.themoviedb.org/"));
                Assert.Equal(0, inner.Calls);
            }
        }

        /// <summary>
        /// The failure message reaches the caller, so it must name the destination without leaking
        /// the path or query — metadata lookups carry title information and API keys.
        /// </summary>
        [Fact]
        public async Task TheBlockMessageNamesTheHostButNotThePath()
        {
            var settings = Settings(failOpen: false);
            var state = StateWith(settings, ProxyHealth.Unreachable);
            var logger = new RecordingLogger();

            using (var invoker = new HttpMessageInvoker(
                       new ProxyGateHandler(new StubHandler(), state, logger, null)))
            {
                var error = await SendAsync(
                    invoker, "https://api.themoviedb.org/3/movie/550?api_key=SECRET&title=Fight+Club");

                Assert.Contains("api.themoviedb.org", error.Message);
                Assert.DoesNotContain("SECRET", error.Message);
                Assert.DoesNotContain("api_key", error.Message);
                Assert.DoesNotContain("Fight", error.Message);

                Assert.DoesNotContain("SECRET", logger.Warnings.Single());
                Assert.DoesNotContain("/3/movie", logger.Warnings.Single());
            }
        }

        // --- Fail-open --------------------------------------------------------------------------

        /// <summary>
        /// Fail-open lets the request out — but never silently. The warning is the whole point of
        /// the option being tolerable at all.
        /// </summary>
        [Fact]
        public async Task FailOpenLetsTheRequestThroughAndSaysSo()
        {
            var settings = Settings(failOpen: true);
            var state = StateWith(settings, ProxyHealth.Unreachable);
            var inner = new StubHandler();
            var logger = new RecordingLogger();

            using (var invoker = new HttpMessageInvoker(
                       new ProxyGateHandler(inner, state, logger, null)))
            {
                Assert.Null(await SendAsync(invoker, "https://api.themoviedb.org/"));
                Assert.Equal(1, inner.Calls);
                Assert.Single(logger.Warnings);
            }
        }

        // --- The quiet paths --------------------------------------------------------------------

        [Fact]
        public async Task AReachableProxyPassesTheRequestThroughWithoutAWarning()
        {
            var settings = Settings();
            var state = StateWith(settings, ProxyHealth.Reachable);
            var inner = new StubHandler();
            var logger = new RecordingLogger();

            using (var invoker = new HttpMessageInvoker(
                       new ProxyGateHandler(inner, state, logger, null)))
            {
                Assert.Null(await SendAsync(invoker, "https://api.themoviedb.org/"));
                Assert.Equal(1, inner.Calls);
                Assert.Empty(logger.Warnings);
            }
        }

        /// <summary>
        /// A bypassed destination is expected behaviour, not an incident — warning on every LAN
        /// request would make the warnings worthless.
        /// </summary>
        [Fact]
        public async Task ABypassedDestinationIsNotWarnedAbout()
        {
            var settings = Settings(failOpen: false);
            var state = StateWith(settings, ProxyHealth.Unreachable);
            var inner = new StubHandler();
            var logger = new RecordingLogger();

            using (var invoker = new HttpMessageInvoker(
                       new ProxyGateHandler(inner, state, logger, null)))
            {
                Assert.Null(await SendAsync(invoker, "http://192.168.1.50:8096/"));
                Assert.Equal(1, inner.Calls);
                Assert.Empty(logger.Warnings);
            }
        }

        [Fact]
        public async Task ADisabledPluginIsSilent()
        {
            var settings = Settings(enabled: false);
            var state = StateWith(settings, ProxyHealth.Unknown);
            var inner = new StubHandler();
            var logger = new RecordingLogger();

            using (var invoker = new HttpMessageInvoker(
                       new ProxyGateHandler(inner, state, logger, null)))
            {
                Assert.Null(await SendAsync(invoker, "https://api.themoviedb.org/"));
                Assert.Equal(1, inner.Calls);
                Assert.Empty(logger.Warnings);
            }
        }

        // --- Throttling -------------------------------------------------------------------------

        /// <summary>
        /// The scan case: thousands of blocked lookups must not become thousands of log lines — and
        /// must still all be blocked.
        /// </summary>
        [Fact]
        public async Task RepeatedBlocksCollapseInTheLogButNotInTheEnforcement()
        {
            var settings = Settings(failOpen: false);
            var state = StateWith(settings, ProxyHealth.Unreachable);
            var inner = new StubHandler();
            var logger = new RecordingLogger();
            var throttle = new LogThrottle(TimeSpan.FromMinutes(1));

            using (var invoker = new HttpMessageInvoker(
                       new ProxyGateHandler(inner, state, logger, throttle)))
            {
                for (var i = 0; i < 200; i++)
                {
                    var error = await SendAsync(
                        invoker, "https://api.themoviedb.org/3/movie/" + i);

                    // Every single one still fails, and still explains itself to its caller.
                    Assert.IsType<HttpRequestException>(error);
                    Assert.Contains("api.themoviedb.org", error.Message);
                }

                Assert.Equal(0, inner.Calls);
                Assert.Single(logger.Warnings);
            }
        }

        /// <summary>
        /// Throttling is per destination, so a second host is reported even while the first is
        /// inside its window.
        /// </summary>
        [Fact]
        public async Task ADifferentHostIsReportedImmediately()
        {
            var settings = Settings(failOpen: false);
            var state = StateWith(settings, ProxyHealth.Unreachable);
            var logger = new RecordingLogger();
            var throttle = new LogThrottle(TimeSpan.FromMinutes(1));

            using (var invoker = new HttpMessageInvoker(
                       new ProxyGateHandler(new StubHandler(), state, logger, throttle)))
            {
                await SendAsync(invoker, "https://api.themoviedb.org/a");
                await SendAsync(invoker, "https://api.themoviedb.org/b");
                await SendAsync(invoker, "https://image.tmdb.org/c");

                Assert.Equal(2, logger.Warnings.Count);
                Assert.Contains(logger.Warnings, w => w.Contains("api.themoviedb.org"));
                Assert.Contains(logger.Warnings, w => w.Contains("image.tmdb.org"));
            }
        }

        /// <summary>
        /// The same host on a different port is a different destination.
        /// </summary>
        [Fact]
        public async Task ADifferentPortIsADifferentDestination()
        {
            var settings = Settings(failOpen: false);
            var state = StateWith(settings, ProxyHealth.Unreachable);
            var logger = new RecordingLogger();
            var throttle = new LogThrottle(TimeSpan.FromMinutes(1));

            using (var invoker = new HttpMessageInvoker(
                       new ProxyGateHandler(new StubHandler(), state, logger, throttle)))
            {
                await SendAsync(invoker, "https://example.com/a");
                await SendAsync(invoker, "https://example.com:8443/a");

                Assert.Equal(2, logger.Warnings.Count);
            }
        }

        [Fact]
        public async Task WithoutAThrottleEveryBlockIsLogged()
        {
            var settings = Settings(failOpen: false);
            var state = StateWith(settings, ProxyHealth.Unreachable);
            var logger = new RecordingLogger();

            using (var invoker = new HttpMessageInvoker(
                       new ProxyGateHandler(new StubHandler(), state, logger, null)))
            {
                await SendAsync(invoker, "https://api.themoviedb.org/a");
                await SendAsync(invoker, "https://api.themoviedb.org/b");

                Assert.Equal(2, logger.Warnings.Count);
            }
        }
    }
}
