using System;
using System.Globalization;
using System.Net;
using EmbyProxyRouter.Localization;

namespace EmbyProxyRouter.Proxy
{
    /// <summary>
    /// A parsed, validated proxy address plus its credentials, in the exact shape .NET wants them.
    /// </summary>
    public sealed class ProxyEndpoint
    {
        /// <summary>The proxy URI handed to .NET. Never carries userinfo — see <see cref="Credential"/>.</summary>
        public Uri Uri { get; private set; }

        /// <summary>Credentials, or null. Always supplied out-of-band, never inside <see cref="Uri"/>.</summary>
        public NetworkCredential Credential { get; private set; }

        public ProxyScheme Scheme { get; private set; }

        public bool IsSocks => Scheme == ProxyScheme.Socks5;

        public string Host => Uri.Host;

        public int Port => Uri.Port;

        /// <summary>
        /// Scheme, host, port and whether credentials are configured — never the password.
        /// </summary>
        /// <remarks>
        /// Ends up embedded in a log line *and* in the reachability detail shown on the settings
        /// page, so it is deferred like everything else with two audiences: the log renders it in
        /// English, the page in the dashboard language.
        /// </remarks>
        public LocalizedText Describe()
        {
            var authority = Uri.Scheme + "://" + Uri.Host + ":" + Uri.Port;

            return Credential == null
                ? LocalizedText.Of("DescribeNoAuth", authority)
                : LocalizedText.Of("DescribeAuthAs", authority, Credential.UserName);
        }

        /// <summary>
        /// Parses the configured address into something .NET will actually honour.
        /// </summary>
        /// <remarks>
        /// Two things here are not cosmetic:
        ///
        /// 1. Credentials are stripped out of the URI and moved into a NetworkCredential. .NET
        ///    silently ignores userinfo in a SOCKS proxy URI — verified against a real SOCKS5 server,
        ///    which saw the client offer only the "no authentication" method. Leaving the password in
        ///    the URI would produce a config that looks authenticated and silently is not.
        ///
        /// 2. An explicit username field always wins over userinfo embedded in the URL, so there is
        ///    one obvious answer when both are filled in.
        /// </remarks>
        public static bool TryParse(
            string address,
            ProxyScheme fallbackScheme,
            string username,
            string password,
            out ProxyEndpoint endpoint,
            out LocalizedText error)
        {
            endpoint = null;
            error = null;

            if (string.IsNullOrWhiteSpace(address))
            {
                error = LocalizedText.Of("ErrNoAddress");
                return false;
            }

            address = address.Trim();

            ProxyScheme scheme;
            string host;
            int port;
            string uriUser = null;
            string uriPassword = null;

            if (address.IndexOf("://", StringComparison.Ordinal) >= 0)
            {
                Uri parsed;
                if (!Uri.TryCreate(address, UriKind.Absolute, out parsed))
                {
                    error = LocalizedText.Of("ErrInvalidUrl", address);
                    return false;
                }

                if (!TryMapScheme(parsed.Scheme, out scheme))
                {
                    error = LocalizedText.Of("ErrUnsupportedScheme", parsed.Scheme);
                    return false;
                }

                host = parsed.Host;
                port = AuthorityHasPort(address) ? parsed.Port : -1;

                if (!string.IsNullOrEmpty(parsed.UserInfo))
                {
                    var split = parsed.UserInfo.Split(new[] { ':' }, 2);
                    uriUser = Uri.UnescapeDataString(split[0]);
                    uriPassword = split.Length > 1 ? Uri.UnescapeDataString(split[1]) : string.Empty;
                }
            }
            else
            {
                scheme = fallbackScheme;

                var colon = address.LastIndexOf(':');
                if (colon <= 0 || colon == address.Length - 1)
                {
                    error = LocalizedText.Of("ErrNeedPort");
                    return false;
                }

                host = address.Substring(0, colon);

                // Invariant and digits-only: the current culture must not decide what a port looks
                // like, and the default NumberStyles.Integer would accept " -1" as a port and let it
                // fall through to the "no explicit port" branch below with the wrong message.
                var portText = address.Substring(colon + 1);
                if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port))
                {
                    error = LocalizedText.Of("ErrPortNotNumber", portText);
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(host))
            {
                error = LocalizedText.Of("ErrNoHost");
                return false;
            }

            if (port < 0)
            {
                error = LocalizedText.Of("ErrNeedExplicitPort");
                return false;
            }

            if (port < 1 || port > 65535)
            {
                error = LocalizedText.Of("ErrPortRange", port);
                return false;
            }

            // UriBuilder validates lazily: the constructor takes any host string and the Uri getter
            // is what rejects one. A TryParse that throws instead of returning false would surface
            // as an exception out of the settings page's Validate and out of OnOptionsSaved, neither
            // of which has anywhere to put it.
            Uri uri;
            try
            {
                uri = new UriBuilder(SchemeToString(scheme), host, port).Uri;
            }
            catch (Exception)
            {
                error = LocalizedText.Of("ErrInvalidHost", host);
                return false;
            }

            NetworkCredential credential = null;
            if (!string.IsNullOrWhiteSpace(username))
            {
                credential = new NetworkCredential(username, password ?? string.Empty);
            }
            else if (!string.IsNullOrEmpty(uriUser))
            {
                credential = new NetworkCredential(uriUser, uriPassword ?? string.Empty);
            }

            endpoint = new ProxyEndpoint
            {
                Uri = uri,
                Credential = credential,
                Scheme = scheme
            };
            return true;
        }

        /// <summary>
        /// Whether the address text itself names a port.
        /// </summary>
        /// <remarks>
        /// This cannot be asked of the parsed <see cref="Uri"/>, which normalises a default port
        /// away: <c>new Uri("https://p:443/").IsDefaultPort</c> is true and the result is
        /// indistinguishable from <c>https://p/</c>. Deriving "the user gave no port" from
        /// <c>IsDefaultPort</c> therefore rejected <c>https://host:443</c> and <c>http://host:80</c>
        /// by telling the user to supply the port they had just supplied. The raw text is the only
        /// place the distinction survives.
        /// </remarks>
        private static bool AuthorityHasPort(string address)
        {
            var start = address.IndexOf("://", StringComparison.Ordinal) + 3;
            if (start >= address.Length)
            {
                return false;
            }

            var authority = address.Substring(start);

            var end = authority.IndexOfAny(new[] { '/', '?', '#' });
            if (end >= 0)
            {
                authority = authority.Substring(0, end);
            }

            var at = authority.LastIndexOf('@');
            if (at >= 0)
            {
                authority = authority.Substring(at + 1);
            }

            var colon = authority.LastIndexOf(':');
            if (colon < 0 || colon == authority.Length - 1)
            {
                return false;
            }

            // An IPv6 literal is bracketed, and every colon inside those brackets belongs to the
            // address rather than introducing a port.
            var bracket = authority.LastIndexOf(']');
            return colon > bracket;
        }

        private static bool TryMapScheme(string value, out ProxyScheme scheme)
        {
            switch ((value ?? string.Empty).ToLowerInvariant())
            {
                case "http":
                    scheme = ProxyScheme.Http;
                    return true;
                case "https":
                    scheme = ProxyScheme.Https;
                    return true;
                case "socks5":
                    scheme = ProxyScheme.Socks5;
                    return true;
                default:
                    scheme = ProxyScheme.Http;
                    return false;
            }
        }

        private static string SchemeToString(ProxyScheme scheme)
        {
            switch (scheme)
            {
                case ProxyScheme.Https:
                    return "https";
                case ProxyScheme.Socks5:
                    return "socks5";
                default:
                    return "http";
            }
        }
    }
}
