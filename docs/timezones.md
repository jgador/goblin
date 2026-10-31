# Workspace timezone

After unlock, when no workspace timezone is saved, Goblin suggests one from the
visitor's public IP through **ipwho.is**. If the lookup fails, is blocked, takes more
than three seconds, or returns an unsupported zone, Goblin falls back to the browser
timezone, then UTC. Confirm with **Use timezone** or choose another zone; detection
never saves a timezone.

The saved choice applies to the whole workspace on every device. Change it under
**Settings → Time & date**: **Detect from IP** checks again, **Use suggestion**
selects the result, and **Save timezone** applies it. Search matches country, city,
IANA ID, or current UTC offset, ignoring case, accents, and extra spaces. The
selector lists the IANA region/city zones that both the Goblin server and the
browser support, plus UTC and the current selection or suggestion. It shows current
offsets; displayed dates follow each zone's daylight-saving and historical rules.

Absolute conversation, Work, execution, system, and log times use this preference;
relative labels stay elapsed time. Stored timestamps and log events keep UTC
instants, and raw JSON and downloads can contain UTC strings. Open Goblin pages pick
up a change on refresh. Reopen or refresh an open log view to apply a change. The
VMUI timezone control can override the current tab until the view reloads.

## Detection and data

With a VPN, IP detection may suggest the VPN exit location; neither the IP nor the
browser timezone proves physical location. The browser calls
`https://ipwho.is/?fields=success,timezone.id` directly, so the provider receives the
visitor's IP and normal request metadata, including the CORS origin. Goblin sends no
credentials or referrer, rejects redirects, and stores only the saved timezone ID.
The free service can be blocked or run out of quota; the fallback still works. See
the [provider documentation](https://ipwhois.io/documentation).

Country and city labels come from bundled IANA tzdb data
(`frontend/src/settings/timezone-places.ts`) and `Intl.DisplayNames`, not from the
location service. To update the data, run
`node scripts/update-timezone-places.mts /usr/share/zoneinfo` with the desired tzdb
version installed. Builds do not read host tzdb files; zones missing from the bundled
data show their IANA ID.

## Storage and verification

The preference is stored in `workspace-preferences.json` in the persistent
application data directory, independent of Work and database availability. Keep the
volume across restarts and upgrades; this file is not a multi-replica configuration
store. Authenticated same-origin saves include the previously read timezone to
prevent stale overwrites, and replace the file atomically. Read or write failures
show an error; a failed first save keeps the setup dialog open.

The log proxy writes the preference to the pinned VictoriaLogs UI's `VLUI:TIMEZONE`
local storage key before VMUI starts, including on direct links. Run the real log UI
check when upgrading VictoriaLogs; see [log verification](logging.md#verification).

Automated tests use controlled responses and do not call the live geolocation
service. A change to the provider also needs a live HTTPS and CORS browser check.
