# Workspace timezone

After the first unlock, Goblin estimates the visitor's timezone from their public
IP using **ipwho.is**. If the request fails, is blocked, takes more than three
seconds, or returns an unsupported timezone, Goblin uses the browser's configured
timezone. If that is unavailable too, the selector starts at **UTC**. Choose
**Use timezone** to confirm the suggestion, or search and select another timezone.
Detection never saves a preference by itself.

For example, a location in the Philippines suggests **Philippines — Manila
(UTC+08:00)**, stored as `Asia/Manila`. Search accepts country names, cities,
IANA IDs, or current UTC offsets, including **Philippines**, **Manila**, or
**Philippines Manila**. Searches ignore case, accents, and extra spaces. A match
count explains the filtered menu; an unmatched current selection appears in its
own group until another option is explicitly chosen. Clearing search restores
the full catalog.

The choice is shared by the whole workspace across browsers and devices. Later
visitors use the saved preference regardless of their browser's timezone or the
server's geographic region. Change it under **Settings → Time & date**. Opening
Settings uses the saved choice without making another IP request. **Detect from
IP** checks the current connection again, which is useful after connecting a VPN.
**Use suggestion** selects the result; **Save timezone** applies it to the workspace.
The list offers the server's IANA region/city timezones supported by the browser,
plus UTC and a recognized suggestion alias when needed. Each zone's daylight
saving and historical offset rules apply to the date being displayed; the offset
beside a choice is its current offset.

## Location and country names

IP geolocation reflects the apparent internet connection. A VPN can therefore
suggest its exit server's timezone. Device location permission (GPS/Wi-Fi) is a
different signal and is not requested. Neither IP location nor the browser's
configured timezone guarantees a visitor's physical location.

The browser calls `https://ipwho.is/?fields=success,timezone.id` directly, so it
uses the visitor's IP rather than the Goblin deployment server's IP. The provider
receives the visitor's IP and normal browser request metadata, including the CORS
origin; Goblin sends no credentials or referrer and requests only the success flag
and timezone ID. Only the confirmed timezone ID is stored in Goblin. Requests use
HTTPS, omit credentials, reject redirects, and stop when the picker is disposed.
The application's connection CSP permits only its own origin and this provider;
scripts still require the application's own origin. Log and cluster viewer CSPs
retain their existing policies.

This uses ipwho.is's keyless endpoint. Its [documentation](https://ipwhois.io/documentation)
currently permits commercial use and describes a 1,000-request daily free limit
(counted per domain for CORS requests). Provider availability, blocking, and quota
exhaustion all use the browser/UTC fallback. No API key or paid subscription is
required for this optional suggestion.

Country/city labels are bundled locally from the public-domain IANA tzdb, with
country names supplied by `Intl.DisplayNames`. They need no location service.
`zone.tab` preserves a country's own city even when its clock rules agree with
another country's. IANA aliases inherit their target's place. Update the
checked-in data using `node scripts/update-timezone-places.mts /usr/share/zoneinfo`
with the desired installed tzdb version; builds never depend on the host's tzdb
files. A timezone introduced after the bundled data uses its readable IANA ID
until the data is updated.

The preference controls absolute times in conversations, Work activity, execution
details, system readings, and the saved log viewer. Relative labels such as
“5 min ago” remain elapsed time. Log events and durable Work timestamps retain
UTC instants. Raw JSON and downloaded log data can still contain UTC strings.

Open Goblin pages pick up changes during their regular refresh. Reopen or refresh
an already open log view to apply a changed workspace timezone. VMUI's own timezone
control can temporarily override that tab; opening or reloading the view restores
the workspace preference. Goblin supplies the setting before VMUI starts, including
on direct links, while keeping its existing script security policy.

## Storage and failure handling

`workspace-preferences.json` in Goblin's persistent application data directory
stores UI configuration independently of Work history and database availability.
The existing application volume must be retained across app restarts and upgrades.
This uses Goblin's current single application process; it is not a configuration
store for multiple independent replicas sharing one directory.

Only authenticated, same-origin requests can save a preference. A write includes
the previously read timezone, so two first visits or stale Settings pages cannot
silently replace another visitor's choice. Saves atomically replace the file;
unreadable configuration and failed writes surface an error. A failed initial
save keeps onboarding open. The suggestion is never saved merely by visiting.

The log adapter targets the pinned VictoriaLogs 1.52.0 storage contract
(`VLUI:TIMEZONE` containing, for example, `{ "value": "Asia/Manila" }`). Its document
response loads an authenticated script before VMUI's bundle. The real log UI check
covers this integration and should be run when upgrading VictoriaLogs.

## Verification

HTTP tests cover authentication, timezone validation, stale and simultaneous
saves, two sessions, restart persistence, and unavailable storage. Browser tests
use the Philippines timezone (`Asia/Manila`) and controlled Philippine IP responses.
They cover IP precedence, browser and UTC fallbacks, timeout and invalid responses,
country/city search, shared settings, explicit confirmation, year-round Philippine
time conversion, mobile layout, and failure recovery. Automated journeys do not
depend on a live geolocation service. The provider's HTTPS/CORS contract also needs
a live browser smoke check when changing providers.
The real VMUI check also covers a new browser opening a direct link and applying
an updated workspace preference on refresh. See [saved logs](logging.md#verification).
