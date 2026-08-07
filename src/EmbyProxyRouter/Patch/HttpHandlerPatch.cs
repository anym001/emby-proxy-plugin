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

        private static ProxyState _state;
        private static DynamicWebProxy _proxy;
        private static ILogger _logger;

        public static bool IsApplied { get; private set; }

        public static string FailureReason { get; private set; }

        /// <summary>
        /// Set when the postfix ran but could not configure a handler it was handed.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="FailureReason"/>, which means the patch never applied at all.
        /// This is the in-between state: Emby is calling us, the fail-closed gate is in place, but
        /// at least one handler is not carrying the proxy. That has to reach the dashboard. Reporting
        /// only "Active" would tell the user their traffic is routed while some of it is not.
        /// </remarks>
        public static string DecorationFailureReason { get; private set; }

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
                IsApplied = false;
                FailureReason = ex.GetBaseException().Message;
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

            IsApplied = true;
            FailureReason = null;
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
            IsApplied = false;
            FailureReason = reason;
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
                DecorationFailureReason = ex.GetBaseException().Message;
                if (_logger != null)
                {
                    _logger.ErrorException("The proxy could not be applied to the HTTP handler.", ex);
                }
            }
        }

        /// <summary>
        /// Attaches the proxy to the handler Emby built, then wraps it in the fail-closed gate.
        /// </summary>
        /// <remarks>
        /// The two halves are separated on purpose. Configuring the inner handler can fail — its
        /// properties freeze after its first request, so assigning <c>Proxy</c> to one that has
        /// already been used throws — while wrapping it cannot. Letting a failure in the first half
        /// skip the second would hand back a bare handler with neither a proxy nor a gate, which
        /// under fail-closed is a silent fail-open: the one outcome this plugin exists to prevent.
        /// So the gate goes on either way, and the failure is recorded for the dashboard.
        /// </remarks>
        private static HttpMessageHandler Decorate(HttpMessageHandler handler)
        {
            if (handler == null)
            {
                return null;
            }

            try
            {
                Configure(handler);
            }
            catch (Exception ex)
            {
                DecorationFailureReason = ex.GetBaseException().Message;
                if (_logger != null)
                {
                    _logger.ErrorException(
                        "The proxy could not be applied to an HTTP handler. The fail-closed gate is " +
                        "still in place, but requests on this handler will not use the proxy.", ex);
                }
            }

            return new ProxyGateHandler(handler, _state, _logger);
        }

        private static void Configure(HttpMessageHandler handler)
        {
            var sockets = handler as SocketsHttpHandler;
            if (sockets != null)
            {
                sockets.Proxy = _proxy;
                sockets.UseProxy = true;

                ApplyCertificatePolicy(sockets);
            }
            else
            {
                // Defensive: should not happen on 4.9.5.0, but a future build could return something
                // else. HttpClientHandler cannot do SOCKS5 — say so rather than fail quietly.
                var legacy = handler as HttpClientHandler;
                if (legacy != null)
                {
                    legacy.Proxy = _proxy;
                    legacy.UseProxy = true;
                    if (_logger != null && _state.Settings.Endpoint != null && _state.Settings.Endpoint.IsSocks)
                    {
                        _logger.Error("Emby returned an HttpClientHandler instead of a SocketsHttpHandler. " +
                                      "SOCKS5 is NOT supported by that handler type.");
                    }
                }
                else if (_logger != null)
                {
                    _logger.Warn("Unknown handler type '" + handler.GetType().FullName +
                                 "' - only the fail-closed gate will be applied.");
                }
            }
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
        /// destinations that go out directly because they are on the bypass list — Emby's licensing
        /// hosts among them. A certificate callback is handed the handshake, not the request, so
        /// nothing here can tell one connection from another. Documented in README.md under "Known
        /// limitations" rather than papered over.
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
