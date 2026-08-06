using System;
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

        /// <summary>
        /// Shows the active failure policy on the page itself.
        /// </summary>
        /// <remarks>
        /// Deliberately surfaced as a status line rather than left implicit in the checkbox below:
        /// whether a dead proxy blocks traffic or silently lets it out is the single most
        /// consequential thing about this plugin, and it should not require reading a config file.
        /// </remarks>
        [DisplayNameL(nameof(Strings.LabelFailurePolicy), typeof(Strings))]
        public StatusItem FailurePolicy { get; set; } = new StatusItem();

        [DisplayNameL(nameof(Strings.LabelPatchStatus), typeof(Strings))]
        public StatusItem PatchStatus { get; set; } = new StatusItem();

        // ---- Language ---------------------------------------------------------------------------

        [DisplayNameL(nameof(Strings.LabelLanguage), typeof(Strings))]
        [DescriptionL(nameof(Strings.DescLanguage), typeof(Strings))]
        public PluginLanguage Language { get; set; } = PluginLanguage.Auto;

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

        // ---- Failure policy ---------------------------------------------------------------------

        [DisplayNameL(nameof(Strings.LabelFailOpen), typeof(Strings))]
        [DescriptionL(nameof(Strings.DescFailOpen), typeof(Strings))]
        public bool AllowDirectWhenProxyUnavailable { get; set; }

        // ---- Bypass -----------------------------------------------------------------------------

        [DisplayNameL(nameof(Strings.LabelBypassList), typeof(Strings))]
        [DescriptionL(nameof(Strings.DescBypassList), typeof(Strings))]
        [EditMultiline(12)]
        public string BypassList { get; set; } = BypassRules.Defaults;

        // ---- Health check -----------------------------------------------------------------------

        [DisplayNameL(nameof(Strings.LabelCheckUrls), typeof(Strings))]
        [DescriptionL(nameof(Strings.DescCheckUrls), typeof(Strings))]
        [EditMultiline(4)]
        public string HealthCheckUrls { get; set; } = ProxyHealthChecker.DefaultUrls;

        [DisplayNameL(nameof(Strings.LabelCheckInterval), typeof(Strings))]
        [DescriptionL(nameof(Strings.DescCheckInterval), typeof(Strings))]
        [MinValue(10)]
        [MaxValue(3600)]
        public int HealthCheckIntervalSeconds { get; set; } = 60;

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
            string error;
            if (!ProxyEndpoint.TryParse(ProxyAddress, Scheme, Username, Password, out endpoint, out error))
            {
                context.AddValidationError(nameof(ProxyAddress), error);
            }

            var rules = BypassRules.Parse(BypassList);
            foreach (var ruleError in rules.Errors)
            {
                context.AddValidationError(nameof(BypassList), ruleError);
            }

            if (HealthCheckIntervalSeconds < 10)
            {
                context.AddValidationError(
                    nameof(HealthCheckIntervalSeconds), Localizer.Get("ErrIntervalMin"));
            }
        }
    }
}
