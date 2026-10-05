# Data accuracy tests

The site's job is to report MPD's numbers correctly. These tests check that at every step.

```
city feed ──import──▶ database ──services──▶ HTML / JSON / CSV
   (1)                  (2)          (3)              (4)
```

| Layer | What is checked | Where |
|---|---|---|
| 1 → 2 | Every feed row is stored once with the right group, Central date and hour, count, place and location. Unplaced rows (0,0) become "no location". Repeated rows are kept. Re-importing changes nothing. A failed page throws. | `ImportFidelityTests`, `UnplacedLocationTests`, `MpdIncidentTests` |
| 2 → feed, over time | Late-posted rows are added, revised rows updated, withdrawn rows removed, in one month or across months. A bad read never deletes a large share. | `ReconcileTests` |
| 2 → 3 | Every figure (crime pages, trends, map, homepage, compare, rates, year-end projection, resident-report separation) against an independent calculation, across many filters. | `ServiceAccuracyTests` |
| 3 → 4 | The rendered homepage, `/crime`, `/trends`, area pages, monthly report, JSON API and CSV downloads against the same independent calculation. Also that every homepage number names its source and which crimes it covers. | `SiteAccuracyTests` (shared checks in `SiteChecks`) |

The independent calculation is `Oracle`. It reads the fake feed (`SyntheticFeed`, seeded, with edge cases: New Year and daylight-saving times, unplaced rows, repeated rows, rows in the 10-day holdback, rows before 2019) with plain loops and shares no code with the services.

## Run

```
dotnet test                              # offline, about 30 seconds; the deploy workflow runs this
```

## Live audit (opt-in)

Checks the real database, the city's real feed and a running site. Skipped unless asked for.

```
export GOTS_AUDIT_DB=local               # or a MySQL connection string
export GOTS_AUDIT_SOURCE=1               # also compare with the city's ArcGIS feed (network)
export GOTS_AUDIT_SITE=http://localhost:5000   # or https://glassonthestreet.com
dotnet test --filter Category=Live
```

- `LiveDatabaseTests`: no duplicate keys, groups and flags consistent with the current rules, dates and hours in range, no row at 0,0, points inside Minneapolis, sane neighborhoods and wards, no collapsed month, resident reports snapped to MPD blocks, car pins in step with the full feed.
- `LiveSourceTests`: every month and every year/offense category equals the city's own statistics.
- `LiveSiteTests`: the same `SiteChecks` as above, with the oracle built from the database. Run after the site's data has settled (it caches its dataset for 30 minutes).

When a number looks wrong on the site, run the live audit first: it says whether the database, the import or the page is at fault.
