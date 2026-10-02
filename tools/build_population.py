#!/usr/bin/env python3
"""Builds src/GlassOnTheStreet.Web/Data/population-2020.json.

Resident population for each Minneapolis neighborhood and ward, from the 2020
Census block counts (Census Bureau TIGERweb, field POP100). Each block's
internal point is assigned to the city's neighborhood and ward polygons with a
point-in-polygon test, then summed. Blocks are the smallest census unit, so
the assignment error is small, but the totals are estimates for the boundaries
used here, not official neighborhood populations.

Sources:
  Census blocks:  tigerweb.geo.census.gov  Census2020/Tracts_Blocks/MapServer/2
  Neighborhoods:  City of Minneapolis, Minneapolis_Neighborhoods feature service
  Wards:          City of Minneapolis, msvcWards_2022 feature service (2022 boundaries)

Run:  python3 tools/build_population.py
"""
import json, urllib.parse, urllib.request, datetime, sys, os

CITY = "https://services.arcgis.com/afSMGVsC7QlRK1kZ/arcgis/rest/services/"
BLOCKS = "https://tigerweb.geo.census.gov/arcgis/rest/services/Census2020/Tracts_Blocks/MapServer/2/query"


def get(url, params):
    full = url + "?" + urllib.parse.urlencode(params)
    for attempt in range(4):
        try:
            return json.load(urllib.request.urlopen(full, timeout=120))
        except Exception as e:  # network blips on the public servers
            if attempt == 3:
                raise
    return None


def polygons(service, name_field):
    out = []
    d = get(CITY + service + "/FeatureServer/0/query", {
        "where": "1=1", "outFields": name_field, "outSR": 4326, "f": "json", "returnGeometry": "true"})
    for f in d["features"]:
        out.append((f["attributes"][name_field], f["geometry"]["rings"]))
    return out


def inside(x, y, rings):
    # Even-odd rule over every ring, so holes are handled.
    hit = False
    for ring in rings:
        n = len(ring)
        j = n - 1
        for i in range(n):
            xi, yi = ring[i]
            xj, yj = ring[j]
            if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi) + xi:
                hit = not hit
            j = i
    return hit


def bbox(rings):
    xs = [p[0] for r in rings for p in r]
    ys = [p[1] for r in rings for p in r]
    return min(xs), min(ys), max(xs), max(ys)


hoods = polygons("Minneapolis_Neighborhoods", "BDNAME")
wards = polygons("msvcWards_2022", "BDNUM")
print("neighborhoods", len(hoods), "wards", len(wards), file=sys.stderr)

# Blocks inside the city's bounding box.
allx = [bbox(r) for _, r in hoods]
minx, miny = min(b[0] for b in allx), min(b[1] for b in allx)
maxx, maxy = max(b[2] for b in allx), max(b[3] for b in allx)
blocks, offset = [], 0
while True:
    d = get(BLOCKS, {
        "where": "STATE='27' AND COUNTY='053' AND POP100>0",
        "geometry": f"{minx},{miny},{maxx},{maxy}", "geometryType": "esriGeometryEnvelope", "inSR": 4326,
        "spatialRel": "esriSpatialRelIntersects", "outFields": "GEOID,POP100,CENTLAT,CENTLON",
        "returnGeometry": "false", "resultOffset": offset, "resultRecordCount": 1000, "f": "json"})
    feats = d.get("features", [])
    blocks += [f["attributes"] for f in feats]
    offset += len(feats)
    if len(feats) < 1000 and not d.get("exceededTransferLimit"):
        break
print("blocks fetched", len(blocks), file=sys.stderr)

hood_boxes = [(n, r, bbox(r)) for n, r in hoods]
ward_boxes = [(n, r, bbox(r)) for n, r in wards]


def assign(x, y, boxes):
    for name, rings, (a, b, c, e) in boxes:
        if a <= x <= c and b <= y <= e and inside(x, y, rings):
            return name
    return None


hood_pop, ward_pop, in_city, outside = {}, {}, 0, 0
for b in blocks:
    try:
        x, y = float(b["CENTLON"]), float(b["CENTLAT"])
    except (TypeError, ValueError):
        continue
    pop = int(b["POP100"])
    h = assign(x, y, hood_boxes)
    w = assign(x, y, ward_boxes)
    if h is None and w is None:
        outside += pop
        continue
    in_city += pop
    if h is not None:
        hood_pop[h] = hood_pop.get(h, 0) + pop
    if w is not None:
        ward_pop[str(w)] = ward_pop.get(str(w), 0) + pop

print("population inside city boundaries:", in_city, "(outside, skipped:", outside, ")", file=sys.stderr)
out = {
    "source": "2020 Census block populations (Census Bureau TIGERweb, POP100) summed into City of Minneapolis neighborhood and ward polygons by block internal point",
    "generated": datetime.date.today().isoformat(),
    "cityTotal": in_city,
    "neighborhoods": dict(sorted(hood_pop.items())),
    "wards": dict(sorted(ward_pop.items(), key=lambda kv: int(kv[0]))),
}
dest = os.path.join(os.path.dirname(__file__), "..", "src", "GlassOnTheStreet.Web", "Data", "population-2020.json")
with open(dest, "w") as fh:
    json.dump(out, fh, indent=1)
print("wrote", os.path.normpath(dest), file=sys.stderr)
