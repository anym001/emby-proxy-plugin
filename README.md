# Emby Proxy Router

Ein minimalistisches Emby-Server-Plugin mit genau einer Aufgabe: den ausgehenden HTTP(S)-Traffic,
den der **Emby-Server-Kern selbst** initiiert, über einen konfigurierbaren Proxy zu leiten —
HTTP, HTTPS oder **SOCKS5** — und dabei private Netze und die Emby-Lizenzserver immer direkt
anzusprechen.

Entwickelt und verifiziert gegen **Emby Server 4.9.5.0** (net8.0).

---

## Was dieses Plugin tut

* Leitet die HTTP-Clients des Emby-Kerns über einen Proxy: Metadaten-Provider (TMDB, TVDB, …),
  Remote-Image-Provider, Untertitel-Downloads.
* Unterstützt **HTTP-, HTTPS- und SOCKS5-Proxies**, umschaltbar per Dropdown oder direkt per
  URL-Schema (`socks5://host:1080`).
* Prüft die Erreichbarkeit des Proxys (TCP-Connect + HTTP-Check über den Proxy) und zeigt das
  Ergebnis im Dashboard an.
* **Fail-Closed als Standard:** Ist der Proxy nicht erreichbar, werden betroffene Requests
  abgebrochen und im Log protokolliert — statt still auf eine Direktverbindung zurückzufallen.
  Fail-Open ist als bewusste Opt-in-Checkbox verfügbar.
* Routet RFC1918, Loopback, Link-local und die Emby-Lizenz-/Connect-Server immer direkt.

## Was dieses Plugin ausdrücklich NICHT tut

Das ist Absicht — der Sinn des Projekts ist eine einzige, überprüfbare Verantwortlichkeit:

* **Keine** Untertitel-Logik, keine Metadaten-Anreicherung, keine Bild-Verarbeitung.
* **Kein** Auto-Update-Mechanismus, keine Telemetrie, keine „Phone-Home"-Funktion.
* **Keine** systemweite Proxy-Konfiguration. Nur Embys eigener HTTP-Stack wird umgeleitet;
  `ffmpeg`, DLNA, Client-Verbindungen und alles andere bleiben unberührt.
* **Kein** Proxy für eingehende Verbindungen. Reverse-Proxy-Betrieb ist ein anderes Thema.
* **Kein** Umgehen der Emby-Lizenzprüfung. Die Lizenzserver stehen bewusst auf der Bypass-Liste.

---

## Verifizierte Grundlagen (Emby 4.9.5.0)

Die folgenden Punkte sind nicht aus Dokumentation übernommen, sondern durch Dekompilieren der
offiziellen `emby-server-deb_4.9.5.0_amd64.deb` und durch Laufzeit-Tests auf .NET 8 belegt.
Sie erklären, warum der Code so aussieht, wie er aussieht.

### Der Patch-Zielpunkt

```csharp
// Emby.Server.Implementations.ApplicationHost
protected virtual HttpMessageHandler CreateHttpClientHandler(HttpMessageHandlerOptions options)
{
    SocketsHttpHandler socketsHttpHandler = new SocketsHttpHandler { ActivityHeadersPropagator = null };
    ...
    return socketsHttpHandler;
}
```

* Der **Rückgabetyp ist `HttpMessageHandler`**, nicht `HttpClientHandler`. Ältere Patches gegen
  diese Methode deklarierten `ref HttpClientHandler __result` — das matcht nicht mehr und ist die
  wahrscheinliche Ursache für „Mod failed"-Meldungen auf 4.9.x.
* Die konkrete Host-Klasse `EmbyServer.CoreAppHost` ist `sealed` und überschreibt die Methode
  **nicht**; der Name kommt in keiner anderen Assembly der Installation vor. Ein Patch auf die
  Basisdeklaration genügt daher.
* Zielframework laut `EmbyServer.runtimeconfig.json`: **net8.0**, self-contained auf .NET 8.0.25.

### SOCKS5-Machbarkeit

`WebProxy`/`HttpClientHandler` können generell kein SOCKS. `SocketsHttpHandler` kann es ab .NET 6 —
und genau den liefert Emby 4.9.5.0. Empirisch gegen einen echten SOCKS5-Server geprüft:

| Verhalten | Ergebnis |
| --- | --- |
| `socks5://` über eigenes `IWebProxy` auf `SocketsHttpHandler` | funktioniert |
| `GetProxy()` wird **pro Request** neu aufgerufen | ja — Basis für Konfigurationsänderungen ohne Neustart |
| Zugangsdaten über `IWebProxy.Credentials` | funktioniert |
| Zugangsdaten als `socks5://user:pass@host:port` | **wird ignoriert** — .NET bietet dann nur „no authentication" an |
| Hostname-Auflösung | geht als ATYP=3 an den Proxy (Remote-DNS, kein DNS-Leak) |

Deshalb zerlegt das Plugin eine eingegebene URL und verschiebt die Zugangsdaten nach
`IWebProxy.Credentials`. Die URL-Schreibweise bleibt als Eingabeformat erlaubt, führt aber sonst zu
einer Konfiguration, die authentifiziert aussieht und es nicht ist.

### Warum ein dynamisches `IWebProxy` statt eines `WebProxy`

Zwei Eigenschaften erzwingen das:

* `CoreHttpClientManager` cacht je einen `HttpClient` samt Handler pro
  `Host + Kompression + Userinfo + Timeout` in einer `ConcurrentDictionary` — **ohne Eviction**.
* `SocketsHttpHandler` friert seine Properties nach dem ersten Request ein; ein späteres Setzen von
  `Proxy` wirft `InvalidOperationException`.

Ein statisch zugewiesener Proxy wäre damit bis zum Serverneustart eingefroren. Weil .NET `GetProxy()`
pro Request aufruft, wirken Änderungen an Adresse, Bypass-Liste und An/Aus dagegen sofort — auch auf
längst gecachten Handlern.

### Warum ein `DelegatingHandler` für Fail-Closed

Ein `IWebProxy` kann nur *einen Proxy wählen* oder `null` zurückgeben — und `null` bedeutet
„direkt verbinden", also genau das Leck, das Fail-Closed verhindern soll. Das Plugin umhüllt den
Handler deshalb zusätzlich mit einem `DelegatingHandler`, der einen Request aktiv abweisen kann.
Das ist unbedenklich, weil `CoreHttpClientManager` das Ergebnis ausschließlich an
`new HttpClient(handler)` weiterreicht und nirgends castet.

---

## Bauen

Voraussetzung: **.NET SDK 8.0** und `curl`, `ar`, `tar`.

```bash
git clone <dieses-repo> emby-proxy-plugin
cd emby-proxy-plugin

# Holt die vier Emby-Referenz-Assemblies (~180 MB Download, nur 4 DLLs bleiben übrig).
./build/fetch-emby-refs.sh

dotnet build -c Release src/EmbyProxyRouter/EmbyProxyRouter.csproj
```

Ergebnis: `src/EmbyProxyRouter/bin/Release/EmbyProxyRouter.dll` — eine **einzelne** Datei.
Harmony ist als eingebettete Ressource enthalten und wird zur Laufzeit geladen; es muss also keine
`0Harmony.dll` mitkopiert werden.

### Warum die Referenz-DLLs nicht im Repo liegen

Es sind proprietäre Emby-Binaries; ihre Weiterverbreitung steht uns nicht zu. Zusätzlich gibt es
für 4.9.5.0 ohnehin kein passendes NuGet-Paket: `MediaBrowser.Server.Core` endet bei 4.9.1.90, und
`Emby.Web.GenericEdit` — nötig für die Einstellungsseite — ist überhaupt nicht auf NuGet.
Das Skript besorgt genau die vier gebrauchten Dateien aus dem offiziellen Release.

Für eine andere Emby-Version:

```bash
FORCE=1 ./build/fetch-emby-refs.sh 4.9.6.0
```

---

## Installation (Unraid / Docker)

Der Emby-Container mappt üblicherweise ein Host-Verzeichnis nach `/config`. Die Plugin-DLL gehört
in dessen `plugins`-Unterordner.

```bash
# Auf dem Unraid-Host, Pfad ggf. anpassen:
PLUGINS=/mnt/user/appdata/emby/plugins

cp EmbyProxyRouter.dll "$PLUGINS/"
chown 99:100 "$PLUGINS/EmbyProxyRouter.dll"
chmod 644   "$PLUGINS/EmbyProxyRouter.dll"

docker restart emby
```

`99:100` ist unter Unraid `nobody:users` — dieselbe UID/GID, unter der der Emby-Container läuft.
Stimmen die Rechte nicht, ignoriert Emby die Datei kommentarlos.

Danach im Dashboard unter **Plugins → Proxy Router** konfigurieren.

### Prüfen, ob der Patch greift

Die Einstellungsseite zeigt oben eine Zeile **Patch-Status**. Steht dort etwas anderes als
„Aktiv", wird **kein** Traffic umgeleitet, und der Grund steht direkt daneben. Im Serverlog:

```
Harmony-Patch aktiv auf HttpMessageHandler ApplicationHost.CreateHttpClientHandler(HttpMessageHandlerOptions) (Emby.Server.Implementations 4.9.5.0).
Proxy Router: aktiviert - socks5://192.168.1.10:1080 (mit Auth (user)) | Fail-Closed | Prüfintervall 60 s
Proxy-Status: ERREICHBAR - HTTP-Check über socks5://192.168.1.10:1080 (mit Auth (user)) erfolgreich (...)
```

---

## Konfiguration

| Feld | Bedeutung |
| --- | --- |
| **Proxy aktivieren** | Aus = Emby verhält sich wie ohne das Plugin. |
| **Proxy-Schema** | `Http`, `Https` oder `Socks5`. Wird nur benutzt, wenn die Adresse kein eigenes Schema mitbringt. |
| **Proxy-Adresse** | `host:port` (z. B. `192.168.1.10:8080`) oder volle URL (z. B. `socks5://192.168.1.10:1080`). Ein Port ist Pflicht. |
| **Benutzername / Passwort** | Optional. Haben Vorrang vor Zugangsdaten in der URL. |
| **Zertifikatsprüfung ignorieren** | Für HTTPS-Proxies mit selbstsigniertem Zertifikat. |
| **Bei Proxy-Ausfall trotzdem direkt verbinden** | Aus (Standard) = Fail-Closed. Ein = Fail-Open. |
| **Bypass-Liste** | Ein Eintrag pro Zeile: CIDR, einzelne IP, Hostname oder `*.example.com`. |
| **Prüf-URLs** | Werden über den Proxy abgerufen; die erste HTTP-2xx-Antwort gilt als „erreichbar". Leer = nur TCP-Check. |
| **Prüfintervall** | Sekunden zwischen den Erreichbarkeitsprüfungen, mindestens 10. |

### Fail-Closed vs. Fail-Open

**Fail-Closed (Standard).** Ist der Proxy nicht erreichbar — oder noch nicht geprüft, oder
fehlerhaft konfiguriert — schlagen betroffene Requests fehl. Jeder Fall landet als Warnung im Log:

```
WARN  Proxy nicht erreichbar - Request blockiert: https://api.themoviedb.org (Proxy ist nicht erreichbar). Fail-Closed ist aktiv; ...
```

Metadaten-Abrufe schlagen dann fehl, solange der Proxy weg ist. Das ist der Preis dafür, dass nichts
unbemerkt am Proxy vorbeigeht.

**Fail-Open (Opt-in).** Requests gehen ohne Proxy direkt raus — aber **nie stillschweigend**:

```
WARN  Fail-Open aktiv - Request geht OHNE Proxy direkt raus: https://api.themoviedb.org (Proxy ist nicht erreichbar)
```

Die aktive Betriebsart wird oben auf der Einstellungsseite als eigene Statuszeile angezeigt.

In den Log-Meldungen stehen bewusst nur Schema, Host und Port — nicht der Pfad. Pfade und
Query-Strings von Metadaten-Abfragen enthalten Titelinformationen und häufig API-Schlüssel.

### Bypass-Standardliste

RFC1918, Loopback und Link-local, dazu die Emby-eigenen Endpunkte. Letztere sind nicht geraten,
sondern aus den 4.9.5.0-Assemblies gelesen:

* `mb3admin.com` — `PluginSecurityManager`: `/admin/service/registration/validate` und
  `/admin/service/appstore/register`; außerdem der Plugin-Katalog in `InstallationManager`
  (`www.mb3admin.com/admin/service/package/...`).
* `connect.emby.media` — `Emby.Server.Connect`: `https://connect.emby.media/service/`.

Lizenz-Traffic unter einer Fail-Closed-Policy durch einen Proxy zu schicken, riskiert die
Emby-Premiere-Aktivierung; das Verschleiern der Lizenz-Identität ist außerdem nicht der Zweck
dieses Plugins. Wer das anders will, kann die Zeilen aus der Liste entfernen.

---

## Bekannte Grenzen

* **Live TV ist nur teilweise erfasst.** `Emby.LiveTV.dll` benutzt sowohl den zentralen
  `IHttpClient` (wird umgeleitet) als auch eigene `HttpClientHandler`-Instanzen (werden **nicht**
  umgeleitet). Wer Live TV nutzt, sollte nicht annehmen, dass dieser Traffic vollständig über den
  Proxy läuft. Eine Sonderbehandlung dafür ist bewusst nicht eingebaut.
* **Die Bypass-Liste löst kein DNS auf.** Hostnamen werden literal verglichen, IP-Regeln greifen nur
  bei IP-Literalen. Eine Auflösung würde für jeden Request eine DNS-Abfrage nach außen erzeugen —
  genau die Sichtbarkeit, die das Plugin vermeiden soll.
* **Proxy-Authentifizierung bei HTTP(S) läuft reaktiv.** .NET sendet die Zugangsdaten erst, nachdem
  der Proxy mit `407` geantwortet hat, nicht präemptiv. Proxies, die ohne Challenge sofort
  ablehnen, funktionieren nicht.
* **„Zertifikatsprüfung ignorieren" wirkt breit.** Die Option deaktiviert die TLS-Prüfung für die
  Verbindung zum Proxy *und* für die darüber getunnelten Zielverbindungen. Nur einschalten, wenn
  der Proxy ein selbstsigniertes Zertifikat verwendet.
* **Zugangsdaten liegen im Klartext.** Emby speichert Plugin-Optionen als JSON unter
  `/config/plugins/configurations/`. Das Passwortfeld ist in der UI maskiert, in der Datei nicht.
* **Bindung an eine interne Emby-Methode.** `CreateHttpClientHandler` ist kein öffentliches API. Ein
  Emby-Update kann es jederzeit ändern. Das Plugin prüft die Signatur beim Start und meldet
  Abweichungen deutlich, statt still nichts zu tun — aber es kann sie nicht reparieren.
* **Requests vor dem ersten Health-Check.** Unter Fail-Closed werden Requests blockiert, bis die
  erste Prüfung durch ist. Das ist so gewollt: eine unbestätigte Proxy-Verfügbarkeit ist kein Grund,
  Traffic durchzulassen.

---

## Projektstruktur

```
build/fetch-emby-refs.sh      Holt die Emby-Referenz-Assemblies
lib/                          Zielordner dafür (nicht eingecheckt)
src/EmbyProxyRouter/
  Plugin.cs                   Einstiegspunkt, Dashboard-Status, Entry Point
  PluginOptions.cs            Einstellungsseite (Emby.Web.GenericEdit)
  Patch/HarmonyLoader.cs      Lädt das eingebettete Harmony
  Patch/HttpHandlerPatch.cs   Der Postfix-Patch inkl. Signaturprüfung
  Proxy/ProxyEndpoint.cs      Adress-Parsing, Credential-Umzug
  Proxy/BypassRules.cs        CIDR-/Host-Matching
  Proxy/ProxySettings.cs      Unveränderlicher Konfigurations-Snapshot
  Proxy/ProxyState.cs         Routing-Entscheidung an einer Stelle
  Proxy/DynamicWebProxy.cs    IWebProxy, pro Request befragt
  Proxy/ProxyGateHandler.cs   Fail-Closed-Durchsetzung, Logging
  Proxy/ProxyHealthChecker.cs Erreichbarkeitsprüfung
  Proxy/ProxyRuntime.cs       Zusammenhalt der Singletons
```

## Lizenz

GPL-3.0 — siehe [LICENSE](LICENSE).

Das Funktionsprinzip (Harmony-Postfix auf Embys interne Handler-Fabrik) ist inspiriert von
[StrmAssistant](https://github.com/sjtuross/StrmAssistant) (GPL-3.0). Der Code hier ist eigenständig
geschrieben; die Lizenz wird aus Respekt vor dieser Herkunft übernommen.
