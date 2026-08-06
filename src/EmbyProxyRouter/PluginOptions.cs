using System;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Validation;
using EmbyProxyRouter.Proxy;
using MediaBrowser.Model.Attributes;

namespace EmbyProxyRouter
{
    /// <summary>
    /// The dashboard settings page, rendered by Emby.Web.GenericEdit.
    /// </summary>
    public class PluginOptions : EditableOptionsBase
    {
        public override string EditorTitle
        {
            get { return "Proxy Router"; }
        }

        public override string EditorDescription
        {
            get
            {
                return "Leitet ausgehenden HTTP(S)-Traffic des Emby-Kerns (Metadaten-Provider, " +
                       "Remote-Bilder, Untertitel-Downloads) über einen Proxy. " +
                       "Private Netze und die Emby-Lizenzserver werden immer direkt angesprochen.";
            }
        }

        // ---- Status (read-only, refreshed each time the page is opened) --------------------------

        [DisplayName("Proxy-Status")]
        public StatusItem ProxyStatus { get; set; } = new StatusItem();

        /// <summary>
        /// Shows the active failure policy on the page itself.
        /// </summary>
        /// <remarks>
        /// Deliberately surfaced as a status line rather than left implicit in the checkbox below:
        /// whether a dead proxy blocks traffic or silently lets it out is the single most
        /// consequential thing about this plugin, and it should not require reading a config file.
        /// </remarks>
        [DisplayName("Verhalten bei Proxy-Ausfall")]
        public StatusItem FailurePolicy { get; set; } = new StatusItem();

        [DisplayName("Patch-Status")]
        public StatusItem PatchStatus { get; set; } = new StatusItem();

        // ---- Proxy ------------------------------------------------------------------------------

        [DisplayName("Proxy aktivieren")]
        [Description("Wenn deaktiviert, verhält sich Emby wie ohne dieses Plugin.")]
        public bool EnableProxy { get; set; }

        [DisplayName("Proxy-Schema")]
        [Description("Wird nur verwendet, wenn die Proxy-Adresse kein eigenes Schema enthält.")]
        public ProxyScheme Scheme { get; set; } = ProxyScheme.Http;

        [DisplayName("Proxy-Adresse")]
        [Description("Host:Port (z. B. 192.168.1.10:8080) oder vollständige URL " +
                     "(z. B. socks5://192.168.1.10:1080). Zugangsdaten bitte in die Felder " +
                     "darunter eintragen - in der URL eingebettete Zugangsdaten werden von .NET " +
                     "bei SOCKS5 ignoriert.")]
        public string ProxyAddress { get; set; } = string.Empty;

        [DisplayName("Benutzername")]
        [Description("Optional. Hat Vorrang vor Zugangsdaten in der URL.")]
        public string Username { get; set; } = string.Empty;

        [DisplayName("Passwort")]
        [IsPassword]
        public string Password { get; set; } = string.Empty;

        [DisplayName("Zertifikatsprüfung ignorieren")]
        [Description("Nötig für HTTPS-Proxies mit selbstsigniertem Zertifikat. Betrifft sowohl die " +
                     "Verbindung zum Proxy als auch die getunnelten Zielverbindungen.")]
        public bool IgnoreCertificateValidation { get; set; }

        // ---- Failure policy ---------------------------------------------------------------------

        [DisplayName("Bei Proxy-Ausfall trotzdem direkt verbinden")]
        [Description("AUS (Standard, Fail-Closed): Ist der Proxy nicht erreichbar, werden betroffene " +
                     "Requests abgebrochen und im Log protokolliert. " +
                     "EIN (Fail-Open): Requests gehen ohne Proxy direkt ins Internet - " +
                     "jeder solche Fall wird als Warnung geloggt.")]
        public bool AllowDirectWhenProxyUnavailable { get; set; }

        // ---- Bypass -----------------------------------------------------------------------------

        [DisplayName("Bypass-Liste")]
        [Description("Ein Eintrag pro Zeile. Erlaubt: CIDR (10.0.0.0/8), einzelne IP, Hostname, " +
                     "Wildcard (*.example.com). Es findet keine DNS-Auflösung statt - Hostnamen " +
                     "werden literal verglichen.")]
        [EditMultiline(12)]
        public string BypassList { get; set; } = BypassRules.Defaults;

        // ---- Health check -----------------------------------------------------------------------

        [DisplayName("Prüf-URLs")]
        [Description("Eine URL pro Zeile. Werden über den Proxy abgerufen; die erste erfolgreiche " +
                     "Antwort (HTTP 2xx) gilt als erreichbar. Leer lassen, um nur per TCP zu prüfen.")]
        [EditMultiline(4)]
        public string HealthCheckUrls { get; set; } = ProxyHealthChecker.DefaultUrls;

        [DisplayName("Prüfintervall (Sekunden)")]
        [Description("Minimum 10 Sekunden.")]
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
                    nameof(HealthCheckIntervalSeconds), "Das Prüfintervall muss mindestens 10 Sekunden betragen.");
            }
        }
    }
}
