namespace EmbyProxyRouter.Proxy
{
    /// <summary>
    /// Proxy protocols this plugin can hand to <see cref="System.Net.Http.SocketsHttpHandler"/>.
    /// </summary>
    /// <remarks>
    /// SOCKS5 is only usable because Emby 4.9.5.0 runs on .NET 8 and its handler factory returns a
    /// SocketsHttpHandler. The older HttpClientHandler/WebProxy combination cannot speak SOCKS at all.
    /// </remarks>
    public enum ProxyScheme
    {
        Http = 0,
        Https = 1,
        Socks5 = 2
    }
}
