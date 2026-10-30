# Workspace timezone

After first unlock, Goblin suggests a timezone from the visitor's public IP via
**ipwho.is**. If detection fails, is blocked, exceeds three seconds, or returns
an unsupported zone, it falls back to the browser timezone, then UTC. Confirm
with **Use timezone** or choose another zone; detection never saves it.

The confirmed choice applies to the whole workspace across devices. Change it
under **Settings → Time & date**; **Detect from IP** checks again, **Use
suggestion** selects the result, and **Save timezone** applies it. Search accepts
country, city, IANA ID, or current UTC offset, ignoring case, accents and extra
spaces. The selector uses browser-supported IANA region/city zones, UTC, and a
recognized suggestion alias when needed. Current offsets appear in the selector;
displayed dates use each zone's daylight-saving and historical rules.

Absolute conversation, Work, execution, system and log times use this preference;
relative labels remain elapsed time. Durable timestamps and log events retain UTC
instants, and raw JSON and downloads can contain UTC strings. Open Goblin pages
pick up changes on refresh. Reopen or refresh an open log view to apply a change.
VMUI's timezone control can override the current tab until the view reloads.

## Detection and data

A VPN may cause IP detection to suggest its exit location; neither IP nor browser
timezone guarantees physical location. The browser requests
`https://ipwho.is/?fields=success,timezone.id` directly, so the provider receives
its IP and normal request metadata including CORS origin. Goblin sends no
credentials or referrer, rejects redirects, and stores only the confirmed timezone
ID. The optional keyless service can be blocked or exhaust its quota; fallback
still works. See [provider documentation](https://ipwhois.io/documentation).

Country and city labels come from bundled IANA tzdb data and `Intl.DisplayNames`,
not the location service. To update it, run
`node scripts/update-timezone-places.mts /usr/share/zoneinfo` with the desired
installed tzdb version. Builds do not depend on host tzdb files; unknown newer
zones use their readable IANA ID.

## Storage and verification

`workspace-preferences.json` in the persistent application data directory stores
the preference independently of Work and database availability. Retain the volume
across restarts and upgrades; this file is not a multi-replica configuration store.
Authenticated same-origin saves include the previously read timezone to prevent
stale overwrites and atomically replace the file. Read or write failures surface
an error; failed initial saves keep onboarding open.

The log adapter supplies the preference to the pinned VictoriaLogs UI via its
`VLUI:TIMEZONE` storage contract before VMUI starts, including on direct links.
Run the real log UI check when upgrading VictoriaLogs; see
[log verification](logging.md#verification).

HTTP and browser tests cover authorization, validation, concurrent saves,
persistence, storage failures, detection fallbacks, search, confirmation, and
time display with controlled responses. They do not depend on a live geolocation
service; changes to the provider also require a live HTTPS/CORS browser smoke
check. Verify real VMUI behavior separately from fixtures.
