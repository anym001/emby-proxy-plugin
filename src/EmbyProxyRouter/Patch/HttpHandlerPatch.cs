using System;
using System.Linq;
using System.Net.Http;
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
        private const string HarmonyId = "de.local.embyproxyrouter";

        private static ProxyState _state;
        private static DynamicWebProxy _proxy;
        private static ILogger _logger;

        public static bool IsApplied { get; private set; }

        public static string FailureReason { get; private set; }

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
                    "Harmony-Patch fehlgeschlagen - ausgehender Traffic wird NICHT über den Proxy geleitet.",
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
                Fail(logger, "Assembly 'Emby.Server.Implementations' ist nicht geladen.");
                return;
            }

            var hostType = assembly.GetType(HostTypeName, throwOnError: false);
            if (hostType == null)
            {
                Fail(logger, "Typ '" + HostTypeName + "' nicht gefunden in " + assembly.GetName().Name +
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
                Fail(logger, "Methode '" + MethodName + "' existiert nicht mehr auf " + HostTypeName +
                             ". Diese Emby-Version wird nicht unterstützt.");
                return;
            }

            var target = candidates.FirstOrDefault(m =>
                m.GetParameters().Length == 1 &&
                typeof(HttpMessageHandler).IsAssignableFrom(m.ReturnType));

            if (target == null)
            {
                var found = string.Join(" | ", candidates.Select(Describe));
                Fail(logger, "Keine passende Überladung von '" + MethodName +
                             "' gefunden. Vorhanden: " + found);
                return;
            }

            var postfix = typeof(HttpHandlerPatch).GetMethod(
                nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic);

            var harmony = new Harmony(HarmonyId);
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));

            IsApplied = true;
            FailureReason = null;
            logger.Info("Harmony-Patch aktiv auf " + Describe(target) +
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
            logger.Error("Harmony-Patch NICHT angewendet: " + reason +
                         " Ausgehender Traffic wird nicht über den Proxy geleitet.");
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
                // Never let a patch failure break Emby's HTTP stack; leave the handler untouched.
                if (_logger != null)
                {
                    _logger.ErrorException("Proxy konnte nicht auf den HTTP-Handler angewendet werden.", ex);
                }
            }
        }

        private static HttpMessageHandler Decorate(HttpMessageHandler handler)
        {
            if (handler == null)
            {
                return null;
            }

            var sockets = handler as SocketsHttpHandler;
            if (sockets != null)
            {
                sockets.Proxy = _proxy;
                sockets.UseProxy = true;

                // Read the setting per-callback rather than baking it in: the handler's properties
                // freeze after its first request, so a snapshot taken here could never be revised,
                // and toggling the option would need a server restart.
                sockets.SslOptions.RemoteCertificateValidationCallback =
                    (sender, certificate, chain, errors) =>
                        errors == System.Net.Security.SslPolicyErrors.None ||
                        _state.Settings.IgnoreCertificateValidation;
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
                        _logger.Error("Emby lieferte einen HttpClientHandler statt SocketsHttpHandler. " +
                                      "SOCKS5 wird von diesem Handler-Typ NICHT unterstützt.");
                    }
                }
                else if (_logger != null)
                {
                    _logger.Warn("Unbekannter Handler-Typ '" + handler.GetType().FullName +
                                 "' - es wird nur die Fail-Closed-Prüfung angewendet.");
                }
            }

            return new ProxyGateHandler(handler, _state, _logger);
        }
    }
}
