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
    /// The gate blocks exactly one thing, and it is the one an IWebProxy cannot express.
    /// </summary>
    /// <remarks>
    /// A resolver can name a proxy or return null, and null means "connect directly". For every
    /// destination with a usable proxy URI that is enough — .NET reaches the proxy or fails, and it
    /// never falls back. The exception is a proxy that is switched on with an address that does not
    /// parse: there is no URI to name, so without a handler that can refuse, those requests would go
    /// out in the clear.
    /// </remarks>
    public class ProxyGateHandlerTests
    {
        private const string Destination = "https://api.themoviedb.org/3/movie/1?api_key=SECRET";

        private static ProxyState StateWith(
            bool enabled = true, string address = "socks5://proxy.example.com:1080")
        {
            var state = new ProxyState();
            state.Apply(ProxySettings.FromOptions(new PluginOptions
            {
                EnableProxy = enabled,
                ProxyAddress = address
            }));
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

        // --- The one blocked case ----------------------------------------------------------------

        /// <summary>
        /// The request must not reach the inner handler at all. Anything else is a leak.
        /// </summary>
        [Fact]
        public async Task AMisconfiguredProxyBlocksBeforeTheInnerHandler()
        {
            var inner = new StubHandler();
            var logger = new RecordingLogger();

            using (var invoker = new HttpMessageInvoker(
                       new ProxyGateHandler(inner, StateWith(address: "nonsense"), logger, null, true)))
            {
                var error = await SendAsync(invoker, Destination);

                Assert.IsType<HttpRequestException>(error);
                Assert.Equal(0, inner.Calls);
                Assert.Single(logger.Errors);
            }
        }

        /// <summary>
        /// The caller is told why, because a socket error from somewhere inside Emby would not say.
        /// </summary>
        [Fact]
        public async Task ABlockedRequestCarriesTheReason()
        {
            using (var invoker = new HttpMessageInvoker(new ProxyGateHandler(
                       new StubHandler(), StateWith(address: "nonsense"), new RecordingLogger(), null, true)))
            {
                var error = await SendAsync(invoker, Destination);
                Assert.Contains("misconfigured", error.Message, StringComparison.OrdinalIgnoreCase);
            }
        }

        // --- Everything else passes through ------------------------------------------------------

        [Theory]
        [InlineData(true, "socks5://proxy.example.com:1080", Destination)]   // routed via the proxy
        [InlineData(true, "socks5://proxy.example.com:1080", "http://127.0.0.1:8096/")] // bypassed
        [InlineData(false, "socks5://proxy.example.com:1080", Destination)]  // plugin switched off
        public async Task EverythingElseReachesTheInnerHandler(bool enabled, string address, string url)
        {
            var inner = new StubHandler();
            var logger = new RecordingLogger();

            using (var invoker = new HttpMessageInvoker(
                       new ProxyGateHandler(inner, StateWith(enabled, address), logger, null, true)))
            {
                Assert.Null(await SendAsync(invoker, url));
                Assert.Equal(1, inner.Calls);

                // A request going where it should is not an incident.
                Assert.Empty(logger.Errors);
                Assert.Empty(logger.Warnings);
            }
        }

        /// <summary>
        /// A disabled plugin must not block even when its address is nonsense — the address is
        /// irrelevant until the proxy is switched on.
        /// </summary>
        [Fact]
        public async Task ADisabledPluginNeverBlocks()
        {
            var inner = new StubHandler();

            using (var invoker = new HttpMessageInvoker(new ProxyGateHandler(
                       inner, StateWith(enabled: false, address: "nonsense"), new RecordingLogger(), null, true)))
            {
                Assert.Null(await SendAsync(invoker, Destination));
                Assert.Equal(1, inner.Calls);
            }
        }

        // --- A handler that never received the proxy ----------------------------------------------

        /// <summary>
        /// Regression: the gate used to pass these straight through to a handler with no proxy.
        /// </summary>
        /// <remarks>
        /// The verdict is <c>ViaProxy</c> and the address is perfectly good, so the block the gate
        /// was originally built for never fired — and the inner handler, which failed to take the
        /// proxy, sent the request straight out. Same leak as the unparseable address, reached from
        /// the other end: routing says "via the proxy" and there is no proxy to go via.
        /// </remarks>
        [Fact]
        public async Task AHandlerThatNeverReceivedTheProxyRefusesWhatWouldHaveUsedIt()
        {
            var inner = new StubHandler();
            var logger = new RecordingLogger();

            using (var invoker = new HttpMessageInvoker(new ProxyGateHandler(
                       inner, StateWith(), logger, null, proxyAttached: false)))
            {
                var error = await SendAsync(invoker, Destination);

                Assert.IsType<HttpRequestException>(error);
                Assert.Equal(0, inner.Calls);
                Assert.Single(logger.Errors);
            }
        }

        /// <summary>
        /// And it says which of the two refusals it was, because the fix is a different one.
        /// </summary>
        [Fact]
        public async Task ThatRefusalNamesTheHandlerRatherThanTheConfiguration()
        {
            using (var invoker = new HttpMessageInvoker(new ProxyGateHandler(
                       new StubHandler(), StateWith(), new RecordingLogger(), null, proxyAttached: false)))
            {
                var error = await SendAsync(invoker, Destination);

                Assert.Contains("could not be attached", error.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("misconfigured", error.Message, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// It refuses only what would have used the proxy. A destination that was going out directly
        /// anyway is unaffected — the handler missing a proxy costs it nothing.
        /// </summary>
        [Theory]
        [InlineData(true, "http://127.0.0.1:8096/")]    // bypassed unconditionally
        [InlineData(false, Destination)]                 // plugin switched off
        public async Task ItRefusesOnlyWhatWouldHaveUsedTheProxy(bool enabled, string url)
        {
            var inner = new StubHandler();
            var logger = new RecordingLogger();

            using (var invoker = new HttpMessageInvoker(new ProxyGateHandler(
                       inner, StateWith(enabled), logger, null, proxyAttached: false)))
            {
                Assert.Null(await SendAsync(invoker, url));
                Assert.Equal(1, inner.Calls);
                Assert.Empty(logger.Errors);
            }
        }

        // --- What reaches the log ----------------------------------------------------------------

        /// <summary>
        /// Paths and query strings of metadata lookups carry title information and API keys.
        /// </summary>
        [Fact]
        public async Task TheLogCarriesSchemeHostAndPortOnly()
        {
            var logger = new RecordingLogger();

            using (var invoker = new HttpMessageInvoker(new ProxyGateHandler(
                       new StubHandler(), StateWith(address: "nonsense"), logger, null, true)))
            {
                var error = await SendAsync(invoker, Destination);

                var line = logger.Errors.Single();
                foreach (var text in new[] { line, error.Message })
                {
                    Assert.Contains("https://api.themoviedb.org", text);
                    Assert.DoesNotContain("SECRET", text);
                    Assert.DoesNotContain("/3/movie/1", text);
                }
            }
        }

        /// <summary>
        /// A misconfigured proxy blocks every request, and a library scan issues thousands.
        /// </summary>
        [Fact]
        public async Task RepeatedBlocksCollapseIntoOneLinePerWindow()
        {
            var logger = new RecordingLogger();
            var throttle = new LogThrottle(TimeSpan.FromMinutes(1));

            using (var invoker = new HttpMessageInvoker(new ProxyGateHandler(
                       new StubHandler(), StateWith(address: "nonsense"), logger, throttle, true)))
            {
                for (var i = 0; i < 20; i++)
                {
                    // Every one of them still fails - only the logging is collapsed.
                    Assert.IsType<HttpRequestException>(await SendAsync(invoker, Destination));
                }
            }

            Assert.Single(logger.Errors);
        }

        /// <summary>
        /// A different destination is a different event and is never suppressed by the first.
        /// </summary>
        [Fact]
        public async Task ADifferentDestinationIsLoggedImmediately()
        {
            var logger = new RecordingLogger();
            var throttle = new LogThrottle(TimeSpan.FromMinutes(1));

            using (var invoker = new HttpMessageInvoker(new ProxyGateHandler(
                       new StubHandler(), StateWith(address: "nonsense"), logger, throttle, true)))
            {
                await SendAsync(invoker, Destination);
                await SendAsync(invoker, "https://image.tmdb.org/t/p/w500/x.jpg");
            }

            Assert.Equal(2, logger.Errors.Count);
        }
    }
}
