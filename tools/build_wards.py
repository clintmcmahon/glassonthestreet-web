#!/usr/bin/env python3
"""Builds src/GlassOnTheStreet.Web/Data/wards-2022.json.

The 13 Minneapolis City Council ward boundaries (2022 redistricting) and the
council member for each, both read from the City of Minneapolis:

  Boundaries:  msvcWards_2022 feature service, requested in WGS84 and
               generalized to about 3 meters (invisible on a map, a third the size)
  Members:     minneapolismn.gov/government/city-council/members/ward-N/
               (name, title and office phone are on each ward's page; email is
               not published there, only a contact form, so it is linked, not copied)

Council members change after elections and ward lines change after each census.
Rerun this and commit the result when either does:  python3 tools/build_wards.py
The page checks that every ward's page names the same ward number, so a layout
change on the city's site fails loudly instead of writing wrong names.
"""
import datetime, html, json, math, os, re, sys, urllib.parse, urllib.request

CITY = "https://services.arcgis.com/afSMGVsC7QlRK1kZ/arcgis/rest/services/"
SITE = "https://www.minneapolismn.gov"
OUT = os.path.join(os.path.dirname(__file__), "..", "src", "GlassOnTheStreet.Web", "Data", "wards-2022.json")
UA = {"User-Agent": "Mozilla/5.0 GlassOnTheStreet-build"}


def fetch(url, params=None):
    if params:
        url += "?" + urllib.parse.urlencode(params)
    return urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=60).read().decode("utf-8", "replace")


def boundaries():
    data = json.loads(fetch(CITY + "msvcWards_2022/FeatureServer/0/query", {
        "where": "1=1", "outFields": "BDNUM", "outSR": 4326, "f": "geojson",
        "maxAllowableOffset": 0.00003, "geometryPrecision": 5,
    }))
    return {int(f["properties"]["BDNUM"]): f["geometry"] for f in data["features"]}


def rings(geometry):
    polys = geometry["coordinates"] if geometry["type"] == "MultiPolygon" else [geometry["coordinates"]]
    return polys


def inside(x, y, ring):
    c = False
    j = len(ring) - 1
    for i in range(len(ring)):
        xi, yi = ring[i]
        xj, yj = ring[j]
        if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi) + xi:
            c = not c
        j = i
    return c


def in_polygon(x, y, poly):
    return inside(x, y, poly[0]) and not any(inside(x, y, hole) for hole in poly[1:])


def dist_to_edge(x, y, poly):
    best = float("inf")
    for ring in poly:
        for (x1, y1), (x2, y2) in zip(ring, ring[1:]):
            dx, dy = x2 - x1, y2 - y1
            t = 0 if dx == dy == 0 else max(0, min(1, ((x - x1) * dx + (y - y1) * dy) / (dx * dx + dy * dy)))
            best = min(best, math.hypot(x - (x1 + t * dx), y - (y1 + t * dy)))
    return best


def label_point(geometry):
    """The point inside the ward farthest from its edge (a grid search), so the label never lands outside a bent ward."""
    best, best_d = None, -1
    for poly in rings(geometry):
        xs = [p[0] for p in poly[0]]
        ys = [p[1] for p in poly[0]]
        steps = 60
        for i in range(steps + 1):
            for j in range(steps + 1):
                x = min(xs) + (max(xs) - min(xs)) * i / steps
                y = min(ys) + (max(ys) - min(ys)) * j / steps
                if in_polygon(x, y, poly):
                    d = dist_to_edge(x, y, poly)
                    if d > best_d:
                        best, best_d = (round(x, 5), round(y, 5)), d
    return list(best)


def member(ward):
    page = fetch(f"{SITE}/government/city-council/members/ward-{ward}/")
    text = html.unescape(re.sub(r"\s+", " ", re.sub(r"<script.*?</script>|<style.*?</style>|<[^>]+>", " ", page, flags=re.S)))
    m = re.search(r"Council (Member|President|Vice-President|Vice President) ([A-Z][\w'’.\- ]+?) represents Ward (\d+)", text)
    if not m or int(m.group(3)) != ward:
        sys.exit(f"Ward {ward}: could not read the council member from the city's page. The page layout may have changed.")
    phone = re.search(rf"612-673-22{ward:02d}\b", text)
    if not phone:
        sys.exit(f"Ward {ward}: office phone not found on the city's page.")
    title = {"Member": "Council Member", "President": "Council President", "Vice-President": "Council Vice President", "Vice President": "Council Vice President"}[m.group(1)]
    return {
        "ward": ward,
        "name": m.group(2).strip(),
        "title": title,
        "phone": phone.group(0),
        "pageUrl": f"{SITE}/government/city-council/members/ward-{ward}/",
        "contactUrl": f"{SITE}/government/city-council/members/ward-{ward}/contact-ward-{ward}/",
    }


def main():
    geoms = boundaries()
    if sorted(geoms) != list(range(1, 14)):
        sys.exit(f"Expected wards 1 to 13, got {sorted(geoms)}")
    wards = []
    for n in range(1, 14):
        w = member(n)
        w["label"] = label_point(geoms[n])
        w["geometry"] = geoms[n]
        wards.append(w)
        print(f"Ward {n:>2}: {w['title']} {w['name']}  {w['phone']}  label {w['label']}")
    out = {
        "source": "City of Minneapolis: msvcWards_2022 (boundaries) and minneapolismn.gov council member pages",
        "retrievedOn": datetime.date.today().isoformat(),
        "wards": wards,
    }
    with open(OUT, "w") as f:
        json.dump(out, f, separators=(",", ":"))
    print(f"Wrote {os.path.getsize(OUT) / 1024:.0f} KB to {os.path.normpath(OUT)}")


main()
