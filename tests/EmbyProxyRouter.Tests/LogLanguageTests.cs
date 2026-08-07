using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbyProxyRouter.Localization;
using EmbyProxyRouter.Proxy;
using Xunit;

namespace EmbyProxyRouter.Tests
{
    /// <summary>
    /// The Emby log is English; only the dashboard follows the display language.
    /// </summary>
    /// <remarks>
    /// A log line is read by whoever is debugging the server — often not the person whose dashboard
    /// language is set, and usually after the fact, pasted into an issue or grepped for a phrase
    /// out of the documentation. Translating it makes it harder to search and harder to hand on.
    ///
    /// The rule is mechanical so it can be checked rather than remembered: keys written to the log
    /// are prefixed <c>Log</c> and exist only in <c>en.json</c>. Anything reaching both audiences is
    /// carried as a <see cref="LocalizedText"/> and rendered per destination.
    /// </remarks>
    public class LogLanguageTests
    {
        private const string Prefix = "EmbyProxyRouter.Localization.";

        private static readonly Assembly PluginAssembly = typeof(Localizer).Assembly;

        /// <summary>Runs <paramref name="body"/> as if the dashboard were set to German.</summary>
        private static void AsGerman(Action body)
        {
            var previous = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = new CultureInfo("de");
                body();
            }
            finally
            {
                CultureInfo.CurrentUICulture = previous;
            }
        }

        private static Dictionary<string, string> LoadLanguage(string code)
        {
            using (var stream = PluginAssembly.GetManifestResourceStream(Prefix + code + ".json"))
            {
                Assert.NotNull(stream);
                using (var document = JsonDocument.Parse(stream))
                {
                    return document.RootElement.EnumerateObject()
                        .Where(p => p.Value.ValueKind == JsonValueKind.String)
                        .ToDictionary(p => p.Name, p => p.Value.GetString(), StringComparer.Ordinal);
                }
            }
        }

        private static IEnumerable<string> ShippedCodes()
        {
            return PluginAssembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) &&
                            n.EndsWith(".json", StringComparison.Ordinal))
                .Select(n => n.Substring(Prefix.Length, n.Length - Prefix.Length - 5));
        }

        // --- The catalogue rule ------------------------------------------------------------------

        /// <summary>
        /// This is the enforcement point. Translating a Log* key would put German into the server
        /// log, and nothing else in the build would notice.
        /// </summary>
        [Fact]
        public void NoTranslationDefinesALogKey()
        {
            foreach (var code in ShippedCodes().Where(c => c != "en"))
            {
                var offending = LoadLanguage(code).Keys
                    .Where(k => k.StartsWith("Log", StringComparison.Ordinal))
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .ToArray();

                Assert.True(
                    offending.Length == 0,
                    code + ".json translates log-only keys, which would put " + code +
                    " into the Emby log: " + string.Join(", ", offending));
            }
        }

        [Fact]
        public void EnglishDefinesEveryLogKeyTheCodeAsksFor()
        {
            var english = LoadLanguage("en");

            foreach (var key in new[]
                     {
                         "LogBlocked", "LogSuppressed",
                         "LogReasonDisabled", "LogReasonMisconfigured",
                         "LogReasonBypassed", "LogReasonProxied"
                     })
            {
                Assert.True(english.ContainsKey(key), "en.json is missing " + key);
            }
        }

        /// <summary>
        /// The other half of the rule: a UI key missing from a translation is fine (it falls back),
        /// but a translation must not invent keys English does not have.
        /// </summary>
        [Fact]
        public void NoTranslationInventsKeysEnglishDoesNotHave()
        {
            var english = LoadLanguage("en");

            foreach (var code in ShippedCodes().Where(c => c != "en"))
            {
                var extra = LoadLanguage(code).Keys
                    .Where(k => !english.ContainsKey(k))
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .ToArray();

                Assert.True(extra.Length == 0,
                    code + ".json defines keys absent from en.json: " + string.Join(", ", extra));
            }
        }

        // --- The resolver ------------------------------------------------------------------------

        [Fact]
        public void GetInvariantIgnoresTheDisplayLanguage()
        {
            var english = Localizer.Get("ProbeDisabled");

            AsGerman(() =>
            {
                // Sanity check that the culture switch actually took, or the assertion below would
                // pass for the wrong reason.
                Assert.NotEqual(english, Localizer.Get("ProbeDisabled"));
                Assert.Equal(english, Localizer.GetInvariant("ProbeDisabled"));
            });
        }

        [Fact]
        public void FormatInvariantIgnoresTheDisplayLanguageToo()
        {
            var english = Localizer.Format("ProbeTcpFailed", "proxy.example.com", 1080, "refused");

            AsGerman(() =>
                Assert.Equal(english, Localizer.FormatInvariant("ProbeTcpFailed", "proxy.example.com", 1080, "refused")));
        }

        // --- LocalizedText -----------------------------------------------------------------------

        [Fact]
        public void LocalizedTextRendersPerDestination()
        {
            var text = LocalizedText.Of("ProbeDisabled");
            var english = text.Invariant();

            AsGerman(() =>
            {
                Assert.Equal(english, text.Invariant());
                Assert.NotEqual(english, text.Localized());
            });
        }

        /// <summary>
        /// Accidental interpolation lands in a log far more often than in the UI, so the implicit
        /// rendering is the English one.
        /// </summary>
        [Fact]
        public void ToStringIsTheEnglishRendering()
        {
            var text = LocalizedText.Of("ProbeDisabled");

            AsGerman(() => Assert.Equal(text.Invariant(), text.ToString()));
        }

        /// <summary>
        /// Messages compose — a reachability detail quotes the endpoint description and the failing
        /// probe's error. A rendered message has to be in one language throughout, not a German
        /// sentence with an English clause inside it.
        /// </summary>
        [Fact]
        public void NestedMessagesFollowTheirParentsLanguage()
        {
            var inner = LocalizedText.Of("ProbeDisabled");
            var outer = LocalizedText.Of("ProbeFailed", inner);

            AsGerman(() =>
            {
                Assert.Contains(inner.Localized(), outer.Localized());
                Assert.Contains(inner.Invariant(), outer.Invariant());
                Assert.DoesNotContain(inner.Localized(), outer.Invariant());
            });
        }

        [Fact]
        public void TheEndpointDescriptionRendersInBothLanguages()
        {
            ProxyEndpoint endpoint;
            LocalizedText error;
            Assert.True(ProxyEndpoint.TryParse(
                "socks5://proxy.example.com:1080", ProxyScheme.Http, null, null,
                out endpoint, out error));

            var english = endpoint.Describe().Invariant();

            AsGerman(() =>
            {
                Assert.Equal(english, endpoint.Describe().Invariant());
                Assert.NotEqual(english, endpoint.Describe().Localized());
            });
        }

        // --- End to end --------------------------------------------------------------------------

        /// <summary>
        /// The whole point, checked through the real gate: a German dashboard must not change one
        /// character of what reaches the log, or of the failure the caller is handed.
        /// </summary>
        [Fact]
        public async Task AGermanDashboardDoesNotChangeWhatTheGateLogs()
        {
            Func<Task<Tuple<string, string>>> run = async () =>
            {
                var settings = ProxySettings.FromOptions(new PluginOptions
                {
                    EnableProxy = true,
                    ProxyAddress = "not a usable address"
                });

                var state = new ProxyState();
                state.Apply(settings);

                var logger = new RecordingLogger();
                using (var invoker = new HttpMessageInvoker(
                           new ProxyGateHandler(new StubHandler(), state, logger, null)))
                using (var request = new HttpRequestMessage(HttpMethod.Get, "https://api.themoviedb.org/x"))
                {
                    string failure = null;
                    try
                    {
                        (await invoker.SendAsync(request, CancellationToken.None)).Dispose();
                    }
                    catch (HttpRequestException ex)
                    {
                        failure = ex.Message;
                    }

                    return Tuple.Create(logger.Errors.Single(), failure);
                }
            };

            var english = await run();

            Tuple<string, string> german = null;
            AsGerman(() => german = run().GetAwaiter().GetResult());

            Assert.Equal(english.Item1, german.Item1);
            Assert.Equal(english.Item2, german.Item2);
            Assert.Contains("misconfigured", english.Item1);
        }

        /// <summary>
        /// And the converse, so the first test cannot pass by the plugin simply never translating
        /// anything: the settings page does follow the display language.
        /// </summary>
        [Fact]
        public void TheSettingsPageStillFollowsTheDisplayLanguage()
        {
            var english = Strings.LabelEnableProxy;

            AsGerman(() => Assert.NotEqual(english, Strings.LabelEnableProxy));
        }

        /// <summary>
        /// A misconfigured address is reported in both places, and each gets its own language.
        /// </summary>
        [Fact]
        public void AConfigErrorIsEnglishInTheLogAndTranslatedOnThePage()
        {
            var settings = ProxySettings.FromOptions(new PluginOptions
            {
                EnableProxy = true,
                ProxyAddress = "nonsense"
            });

            Assert.NotNull(settings.ConfigError);
            var english = settings.ConfigError.Invariant();

            AsGerman(() =>
            {
                Assert.Equal(english, settings.ConfigError.Invariant());
                Assert.NotEqual(english, settings.ConfigError.Localized());
            });
        }
    }
}
