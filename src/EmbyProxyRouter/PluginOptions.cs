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
        /// <summary>
        /// The bounds on <see cref="HealthCheckIntervalSeconds"/>, in one place.
        /// </summary>
        /// <remarks>
        /// Named constants rather than literals because three things have to agree about them: the
        /// spinner's range on the page, the validation error, and the clamp in
        /// <see cref="ProxySettings.FromOptions"/> that catches a value the page never saw. They did
        /// not agree — the page advertised a 3600-second ceiling that nothing enforced, so an
        /// interval edited straight into the options JSON was taken verbatim.
        ///
        /// Both ends have a reason. Below ten seconds the checks cost more than they establish, and
        /// every one of them shows the proxy's egress address to the check URL. Above an hour a
        /// recovered proxy stays marked unreachable — and under fail-closed, traffic stays blocked —
        /// for long enough that the plugin looks broken rather than cautious.
        /// </remarks>
        public const int MinCheckIntervalSeconds = 10;

        /// <summary>Upper bound on the check interval, in seconds. See <see cref="MinCheckIntervalSeconds"/>.</summary>
        public const int MaxCheckIntervalSeconds = 3600;

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
        [EditMultiline(6)]
        public string BypassList { get; set; } = string.Empty;

        // ---- Health check -----------------------------------------------------------------------

        /// <summary>
        /// Split by scheme rather than offered as a list, because the two probes are not
        /// interchangeable and both are required.
        /// </summary>
        /// <remarks>
        /// A free-form list would let someone enter two HTTPS URLs and believe the forwarding path
        /// was covered. Two named fields make that impossible, and they can be validated per scheme.
        /// Either may be cleared to skip that probe; clearing both leaves the TCP check only.
        /// </remarks>
        [DisplayNameL(nameof(Strings.LabelCheckUrlHttp), typeof(Strings))]
        [DescriptionL(nameof(Strings.DescCheckUrlHttp), typeof(Strings))]
        public string HealthCheckUrlHttp { get; set; } = ProxyHealthChecker.DefaultHttpUrl;

        [DisplayNameL(nameof(Strings.LabelCheckUrlHttps), typeof(Strings))]
        [DescriptionL(nameof(Strings.DescCheckUrlHttps), typeof(Strings))]
        public string HealthCheckUrlHttps { get; set; } = ProxyHealthChecker.DefaultHttpsUrl;

        [DisplayNameL(nameof(Strings.LabelCheckInterval), typeof(Strings))]
        [DescriptionL(nameof(Strings.DescCheckInterval), typeof(Strings))]
        [MinValue(MinCheckIntervalSeconds)]
        [MaxValue(MaxCheckIntervalSeconds)]
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

            ValidateCheckUrl(context, nameof(HealthCheckUrlHttp), HealthCheckUrlHttp, "http");
            ValidateCheckUrl(context, nameof(HealthCheckUrlHttps), HealthCheckUrlHttps, "https");

            if (HealthCheckIntervalSeconds < MinCheckIntervalSeconds)
            {
                context.AddValidationError(
                    nameof(HealthCheckIntervalSeconds),
                    Localizer.Format("ErrIntervalMin", MinCheckIntervalSeconds));
            }
            else if (HealthCheckIntervalSeconds > MaxCheckIntervalSeconds)
            {
                context.AddValidationError(
                    nameof(HealthCheckIntervalSeconds),
                    Localizer.Format("ErrIntervalMax", MaxCheckIntervalSeconds));
            }
        }

        /// <summary>
        /// Rejects a URL whose scheme does not match the field it was entered in.
        /// </summary>
        /// <remarks>
        /// An HTTPS URL in the HTTP field would silently probe the CONNECT tunnel twice and leave the
        /// forwarding path unchecked - the exact blind spot the two fields exist to close. Empty is
        /// allowed and means "skip this probe".
        /// </remarks>
        private static void ValidateCheckUrl(
            ValidationContext context, string field, string value, string expectedScheme)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            Uri url;
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out url))
            {
                context.AddValidationError(field, Localizer.Format("HealthInvalidUrl", value.Trim()));
                return;
            }

            if (!string.Equals(url.Scheme, expectedScheme, StringComparison.OrdinalIgnoreCase))
            {
                context.AddValidationError(
                    field, Localizer.Format("ErrCheckUrlScheme", expectedScheme + "://"));
            }
        }
    }
}
