using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Validation;
using EmbyProxyRouter.Localization;
using EmbyProxyRouter.Proxy;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;

namespace EmbyProxyRouter
{
    /// <summary>
    /// The dashboard settings page, rendered by Emby.Web.GenericEdit.
    /// </summary>
    /// <remarks>
    /// Labels and descriptions go through <see cref="Strings"/> rather than literal
    /// <c>[DisplayName]</c> text, so the page follows the selected language. See
    /// <see cref="Localizer"/> for how that indirection works.
    /// </remarks>
    public class PluginOptions : EditableOptionsBase
    {
        public override string EditorTitle
        {
            get { return Localizer.Get("EditorTitle"); }
        }

        public override string EditorDescription
        {
            get { return Localizer.Get("EditorDescription"); }
        }

        // ---- Status (read-only, refreshed each time the page is opened) --------------------------

        [DisplayNameL(nameof(Strings.LabelProxyStatus), typeof(Strings))]
        public StatusItem ProxyStatus { get; set; } = new StatusItem();

        [DisplayNameL(nameof(Strings.LabelPatchStatus), typeof(Strings))]
        public StatusItem PatchStatus { get; set; } = new StatusItem();

        // ---- Proxy ------------------------------------------------------------------------------

        [DisplayNameL(nameof(Strings.LabelEnableProxy), typeof(Strings))]
        [DescriptionL(nameof(Strings.DescEnableProxy), typeof(Strings))]
        public bool EnableProxy { get; set; }

        [DisplayNameL(nameof(Strings.LabelScheme), typeof(Strings))]
        [DescriptionL(nameof(Strings.DescScheme), typeof(Strings))]
        public ProxyScheme Scheme { get; set; } = ProxyScheme.Http;

        [DisplayNameL(nameof(Strings.LabelProxyAddress), typeof(Strings))]
        [DescriptionL(nameof(Strings.DescProxyAddress), typeof(Strings))]
        public string ProxyAddress { get; set; } = string.Empty;

        [DisplayNameL(nameof(Strings.LabelUsername), typeof(Strings))]
        [DescriptionL(nameof(Strings.DescUsername), typeof(Strings))]
        public string Username { get; set; } = string.Empty;

        [DisplayNameL(nameof(Strings.LabelPassword), typeof(Strings))]
        [IsPassword]
        public string Password { get; set; } = string.Empty;

        [DisplayNameL(nameof(Strings.LabelIgnoreCert), typeof(Strings))]
        [DescriptionL(nameof(Strings.DescIgnoreCert), typeof(Strings))]
        public bool IgnoreCertificateValidation { get; set; }

        // ---- Bypass -----------------------------------------------------------------------------

        /// <summary>
        /// Whether RFC1918, link-local, ULA and <c>*.local</c> skip the proxy.
        /// </summary>
        /// <remarks>
        /// Off by default: everything, without exception, goes through the proxy, which is the
        /// answer that needs no qualification for a plugin whose job is to route outbound traffic.
        /// Switching it on is for a server that also talks to its own LAN — other Emby servers,
        /// DLNA endpoints, a local metadata cache — and does not want that traffic crossing a
        /// remote proxy, or dying with it.
        ///
        /// Note what the default costs: an unreachable proxy takes the LAN with it. Loopback is
        /// never affected either way; see <see cref="BypassRules.Always"/>.
        /// </remarks>
        [DisplayNameL(nameof(Strings.LabelBypassPrivate), typeof(Strings))]
        [DescriptionL(nameof(Strings.DescBypassPrivate), typeof(Strings))]
        public bool BypassPrivateNetworks { get; set; }

        [DisplayNameL(nameof(Strings.LabelBypassList), typeof(Strings))]
        [DescriptionL(nameof(Strings.DescBypassList), typeof(Strings))]
        [EditMultiline(6)]
        public string BypassList { get; set; } = string.Empty;

        /// <summary>
        /// Rejects a configuration that would not do what it appears to do.
        /// </summary>
        protected override void Validate(ValidationContext context)
        {
            base.Validate(context);

            if (!EnableProxy)
            {
                return;
            }

            ProxyEndpoint endpoint;
            LocalizedText error;
            if (!ProxyEndpoint.TryParse(ProxyAddress, Scheme, Username, Password, out endpoint, out error))
            {
                context.AddValidationError(nameof(ProxyAddress), error.Localized());
            }

            var rules = BypassRules.Parse(BypassList, BypassPrivateNetworks);
            foreach (var ruleError in rules.Errors)
            {
                context.AddValidationError(nameof(BypassList), ruleError);
            }
        }
    }
}
