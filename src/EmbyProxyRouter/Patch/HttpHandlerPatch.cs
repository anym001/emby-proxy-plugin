using System;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using EmbyProxyRouter.Proxy;
using HarmonyLib;
using MediaBrowser.Model.Logging;

namespace EmbyProxyRouter.Patch
{
    /// <summary>
    /// Patches Emby's internal HTTP handler factory so core outbound traffic honours the proxy.
    /// </summary>
    /// <remarks>
    /// Target, verified by decompiling Emby.Server.Implementations.dll from the official
    /// emby-server-deb_4.9.5.0_amd64.deb:
    ///
    ///     protected virtual HttpMessageHandler CreateHttpClientHandler(HttpMessageHandlerOptions options)
    ///
    /// declared on Emby.Server.Implementations.ApplicationHost, returning a SocketsHttpHandler.
    ///
    /// Two details are easy to get wrong and worth stating:
    ///
    ///   * The return type is HttpMessageHandler, not HttpClientHandler. Older patches against this
    ///     method declared `ref HttpClientHandler __result`, which no longer matches and fails to
    ///     apply — the likely cause of "Mod failed" reports on 4.9.x.
    ///   * The concrete host, EmbyServer.CoreAppHost, is sealed and does not override the method, so
    ///     patching the base declaration is sufficient. Confirmed: the name occurs in no other
    ///     assembly in the install.
    ///
    /// Everything here reports precisely what it found on failure. A patch that silently does nothing
    /// is worse than one that refuses loudly, because the user cannot tell that traffic is escaping.
    /// </remarks>
    internal static class HttpHandlerPatch
    {
        private const string HostTypeName = "Emby.Server.Implementations.ApplicationHost";
        private const string MethodName = "CreateHttpClientHandler";
        private const string HarmonyId = "org.embyproxyrouter.plugin";

        /// <summary>
        /// How long the gate collapses identical routing warnings for.
        /// </summary>
        /// <remarks>
        /// One minute is chosen against the failure it exists for: a library scan issuing thousands
        /// of lookups against a dead proxy. Short enough that the log still shows the problem is
        /// ongoing rather than a one-off, long enough that a scan produces a handful of lines per
        /// destination instead of one per request.
        /// </remarks>
        private static readonly TimeSpan WarningWindow = TimeSpan.FromMinutes(1);

        /// <summary>
        /// One throttle for every gate. Emby caches a handler per host, so a per-instance throttle
        /// would see one destination each and collapse nothing.
        /// </summary>
        private static readonly LogThrottle Throttle = new LogThrottle(WarningWindow);

        /// <summary>
        /// How long a connect attempt may take before it is given up on.
        /// </summary>
        /// <remarks>
        /// .NET defaults SocketsHttpHandler.ConnectTimeout to infinite, leaving only
        /// HttpClient.Timeout to bound it. That is tolerable when a failed connect means one failed
        /// request, and it is not once every request goes to a single proxy: a proxy that drops
        /// packets rather than refusing them — a VPN interface that went away is the usual shape —
        /// then hangs every request for the full HTTP timeout, and a library scan stops rather than
        /// fails. A refused connection is instant either way; this only bounds the silent case.
        ///
        /// Set on the handler because the plugin has already claimed responsibility for where this
        /// traffic goes. Fifteen seconds is far beyond any healthy proxy on a LAN or over a tunnel,
        /// and far below the hundred seconds it replaces.
        /// </remarks>
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

        private static ProxyState _state;
        private static DynamicWebProxy _proxy;
        private static ILogger _logger;

        // Written from whichever thread Emby happens to build a handler on and read by the settings
        // page on another, so the reads are declared volatile rather than left to chance. Backing
        // fields because an auto-property cannot be.
        private static volatile bool _isApplied;
        private static volatile string _failureReason;
        private static volatile string _decorationFailureReason;

        public static bool IsApplied
        {
            get { return _isApplied; }
        }

        public static string FailureReason
        {
            get { return _failureReason; }
        }

        /// <summary>
        /// Set when the postfix ran but could not attach the proxy to a handler it was handed.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="FailureReason"/>, which means the patch never applied at all.
        /// This is the in-between state: Emby is calling us, the gate is in place, but at least one
        /// handler is not carrying the proxy. Requests on that handler are refused by the gate
        /// rather than sent out unproxied, so nothing leaks — but they do not reach the proxy
        /// either, and that has to reach the dashboard. Reporting plain "Active" would tell the user
        /// their traffic is routed while some of it is failing.
        /// </remarks>
        public static string DecorationFailureReason
        {
            get { return _decorationFailureReason; }
        }

        public static void Apply(ILogger logger, ProxyState state, DynamicWebProxy proxy)
        {
            _logger = logger;
            _state = state;
            _proxy = proxy;

            HarmonyLoader.EnsureResolverInstalled(logger);

            try
            {
                ApplyCore(logger);
            }
            catch (Exception ex)
            {
                _isApplied = false;
                _failureReason = ex.GetBaseException().Message;
                logger.ErrorException(
                    "Harmony patch failed - outbound traffic is NOT being routed through the proxy.",
                    ex);
            }
        }

        /// <summary>
        /// Kept separate and never inlined so the Harmony assembly resolver is guaranteed to be in
        /// place before the JIT has to resolve any HarmonyLib type mentioned in this method body.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ApplyCore(ILogger logger)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(
                    a.GetName().Name, "Emby.Server.Implementations", StringComparison.OrdinalIgnoreCase));

            if (assembly == null)
            {
                Fail(logger, "Assembly 'Emby.Server.Implementations' is not loaded.");
                return;
            }

            var hostType = assembly.GetType(HostTypeName, throwOnError: false);
            if (hostType == null)
            {
                Fail(logger, "Type '" + HostTypeName + "' not found in " + assembly.GetName().Name +
                             " " + assembly.GetName().Version + ".");
                return;
            }

            var candidates = hostType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                            BindingFlags.DeclaredOnly)
                .Where(m => string.Equals(m.Name, MethodName, StringComparison.Ordinal))
                .ToArray();

            if (candidates.Length == 0)
            {
                Fail(logger, "Method '" + MethodName + "' no longer exists on " + HostTypeName +
                             ". This Emby version is not supported.");
                return;
            }

            var target = candidates.FirstOrDefault(m =>
                m.GetParameters().Length == 1 &&
                typeof(HttpMessageHandler).IsAssignableFrom(m.ReturnType));

            if (target == null)
            {
                var found = string.Join(" | ", candidates.Select(Describe));
                Fail(logger, "No matching overload of '" + MethodName +
                             "' found. Present: " + found);
                return;
            }

            var postfix = typeof(HttpHandlerPatch).GetMethod(
                nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic);

            var harmony = new Harmony(HarmonyId);
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));

            _isApplied = true;
            _failureReason = null;
            logger.Info("Harmony patch active on " + Describe(target) +
                        " (Emby.Server.Implementations " + assembly.GetName().Version + ").");
        }

        private static string Describe(MethodInfo method)
        {
            var parameters = string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name));
            return method.ReturnType.Name + " " + method.DeclaringType.Name + "." + method.Name +
                   "(" + parameters + ")";
        }

        private static void Fail(ILogger logger, string reason)
        {
            _isApplied = false;
            _failureReason = reason;
            logger.Error("Harmony patch NOT applied: " + reason +
                         " Outbound traffic is not being routed through the proxy.");
        }

        /// <summary>
        /// Runs after Emby builds a handler. The parameter type must match the patched method's
        /// return type exactly, which is HttpMessageHandler.
        /// </summary>
        private static void Postfix(ref HttpMessageHandler __result)
        {
            try
            {
                __result = Decorate(__result);
            }
            catch (Exception ex)
            {
                // Last resort. Decorate already handles the failure it can anticipate and still
                // returns a gated handler; reaching here means something unforeseen, and breaking
                // Emby's HTTP stack outright would be worse than leaving the handler untouched.
                _decorationFailureReason = ex.GetBaseException().Message;
                if (_logger != null)
                {
                    _logger.ErrorException("The proxy could not be applied to the HTTP handler.", ex);
                }
            }
        }

        /// <summary>
        /// Attaches the proxy to the handler Emby built, then wraps it in the gate.
        /// </summary>
        /// <remarks>
        /// The two halves are separated on purpose. Configuring the inner handler can fail — its
        /// properties freeze after its first request, so assigning <c>Proxy</c> to one that has
        /// already been used throws — while wrapping it cannot. Letting a failure in the first half
        /// skip the second would hand back a bare handler with neither a proxy nor a gate — every
        /// request on it going out in the clear, which is the one outcome this plugin exists to
        /// prevent. So the gate goes on either way.
        ///
        /// The gate is also told which of the two it is wrapping. A gate that only knew about the
        /// unparseable-address verdict would pass these requests straight through to a handler with
        /// no proxy on it, which is the same leak reached from the other end: the routing verdict
        /// says "via the proxy" and there is no proxy to go via. Whether the attach succeeded is
        /// settled here, once, and cannot change afterwards.
        /// </remarks>
        private static HttpMessageHandler Decorate(HttpMessageHandler handler)
        {
            if (handler == null)
            {
                return null;
            }

            bool proxyAttached;
            try
            {
                proxyAttached = Configure(handler);
            }
            catch (Exception ex)
            {
                proxyAttached = false;
                _decorationFailureReason = ex.GetBaseException().Message;
                if (_logger != null)
                {
                    _logger.ErrorException(
                        "The proxy could not be applied to an HTTP handler. The gate is still in " +
                        "place and will refuse requests on it rather than send them unproxied.", ex);
                }
            }

            return new ProxyGateHandler(handler, _state, _logger, Throttle, proxyAttached);
        }

        /// <summary>Returns whether the handler came away carrying the proxy.</summary>
        private static bool Configure(HttpMessageHandler handler)
        {
            var sockets = handler as SocketsHttpHandler;
            if (sockets != null)
            {
                sockets.Proxy = _proxy;
                sockets.UseProxy = true;
                sockets.ConnectTimeout = ConnectTimeout;

                ApplyCertificatePolicy(sockets);
                return true;
            }

            // Defensive: should not happen on 4.9.5.0, but a future build could return something
            // else. HttpClientHandler cannot do SOCKS5 — say so rather than fail quietly.
            var legacy = handler as HttpClientHandler;
            if (legacy != null)
            {
                legacy.Proxy = _proxy;
                legacy.UseProxy = true;

                // One snapshot: reading Settings twice around a save could pair the handler type
                // with an endpoint from a different configuration.
                var endpoint = _state.Settings.Endpoint;
                if (_logger != null && endpoint != null && endpoint.IsSocks)
                {
                    _logger.Error("Emby returned an HttpClientHandler instead of a SocketsHttpHandler. " +
                                  "SOCKS5 is NOT supported by that handler type.");
                }

                return true;
            }

            // Nothing here can attach a proxy to a handler of unknown shape, so the gate has to
            // refuse for it. Recorded rather than only logged: the dashboard is where someone finds
            // out why their requests are failing.
            _decorationFailureReason =
                "Emby returned an unsupported HTTP handler type '" + handler.GetType().FullName + "'.";
            if (_logger != null)
            {
                _logger.Warn("Unknown handler type '" + handler.GetType().FullName +
                             "' - the proxy cannot be attached to it, so the gate will refuse " +
                             "requests on it rather than let them out unproxied.");
            }

            return false;
        }

        /// <summary>
        /// Installs the certificate policy for the "ignore certificate validation" option.
        /// </summary>
        /// <remarks>
        /// Two things this deliberately does not do:
        ///
        ///   * It does not discard a callback Emby already installed. Overwriting one outright would
        ///     silently drop whatever policy the server had — the plugin's job is to relax validation
        ///     when asked to, not to replace the server's own decision when it was not.
        ///   * It does not relax anything unless the proxy is actually configured and switched on.
        ///     The option exists for proxies that present a self-signed certificate, so with no proxy
        ///     in play there is nothing for it to excuse, and a disabled plugin has to leave Emby's
        ///     TLS behaviour exactly as it found it.
        ///
        /// What it still cannot narrow: with the proxy switched on, the relaxation also covers the
        /// destinations that go out directly because they are on the bypass list. A certificate
        /// callback is handed the handshake, not the request, so nothing here can tell one
        /// connection from another. Documented in README.md under "Known limitations" rather than
        /// papered over.
        ///
        /// The setting is read per callback rather than baked in, because the handler's properties
        /// freeze after its first request: a value captured here could never be revised, and
        /// toggling the option would need a server restart.
        /// </remarks>
        private static void ApplyCertificatePolicy(SocketsHttpHandler sockets)
        {
            var inner = sockets.SslOptions.RemoteCertificateValidationCallback;

            sockets.SslOptions.RemoteCertificateValidationCallback =
                (sender, certificate, chain, errors) =>
                {
                    if (errors != SslPolicyErrors.None && ShouldIgnoreCertificateErrors())
                    {
                        return true;
                    }

                    return inner != null
                        ? inner(sender, certificate, chain, errors)
                        : errors == SslPolicyErrors.None;
                };
        }

        private static bool ShouldIgnoreCertificateErrors()
        {
            var settings = _state.Settings;
            return settings.IgnoreCertificateValidation &&
                   settings.Enabled &&
                   settings.Endpoint != null;
        }
    }
}
