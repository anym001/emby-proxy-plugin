using System;
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

        public string Describe()
        {
            var auth = Credential == null
                ? Localizer.Get("AuthNone")
                : Localizer.Format("AuthAs", Credential.UserName);
            return Uri.Scheme + "://" + Uri.Host + ":" + Uri.Port + " (" + auth + ")";
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
            out string error)
        {
            endpoint = null;
            error = null;

            if (string.IsNullOrWhiteSpace(address))
            {
                error = Localizer.Get("ErrNoAddress");
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
                    error = Localizer.Format("ErrInvalidUrl", address);
                    return false;
                }

                if (!TryMapScheme(parsed.Scheme, out scheme))
                {
                    error = Localizer.Format("ErrUnsupportedScheme", parsed.Scheme);
                    return false;
                }

                host = parsed.Host;
                port = parsed.IsDefaultPort ? -1 : parsed.Port;

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
                    error = Localizer.Get("ErrNeedPort");
                    return false;
                }

                host = address.Substring(0, colon);
                if (!int.TryParse(address.Substring(colon + 1), out port))
                {
                    error = Localizer.Format("ErrPortNotNumber", address.Substring(colon + 1));
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(host))
            {
                error = Localizer.Get("ErrNoHost");
                return false;
            }

            if (port < 0)
            {
                error = Localizer.Get("ErrNeedExplicitPort");
                return false;
            }

            if (port < 1 || port > 65535)
            {
                error = Localizer.Format("ErrPortRange", port);
                return false;
            }

            var builder = new UriBuilder(SchemeToString(scheme), host, port);

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
                Uri = builder.Uri,
                Credential = credential,
                Scheme = scheme
            };
            return true;
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
