// /map: MPD offenses aggregated to the block and drawn with clusters, hover cards and an optional
// heatmap, plus resident-submitted reports on top. Mapbox GL JS draws it when a token is configured
// (window.GOTS_MAP.engine === "mapbox"); otherwise the open-source MapLibre build is used. The two
// share one API, so everything below runs on either. Every string that comes from the API goes
// into the page through esc().
(function () {
  "use strict";

  var cfg = window.GOTS_MAP || { engine: "maplibre" };
  var isMapbox = cfg.engine === "mapbox";
  var GL = isMapbox ? window.mapboxgl : window.maplibregl;
  var frame = document.getElementById("map-frame");
  if (!GL || !frame) return;

  var fmt = function (n) { return formatNumber(n); };
  var MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

  // One orange ramp, light to dark, that matches the rest of the site. Residents get their own
  // colors (blue and ink) so they never read as part of the ramp.
  var RAMP = ["#fde7d3", "#f8b788", "#f08a45", "#ea580c", "#9a3412"];
  var RADII = [4, 5.5, 8, 12, 18];
  var RESIDENT = { WindowSmashed: "#2a78d6", Rifled: "#18181b" };
  var INK = "#18181b";
  var EMPTY = { type: "FeatureCollection", features: [] };
  var CLUSTER_DETAIL_LIMIT = 2000;

  var state = { group: "car", range: "30", from: "", to: "", mode: "clusters", residents: true };
  var meta = null;       // meta block of the latest /api/map/blocks response
  var requestId = 0;
  var hoverPopup = null;
  var stickyPopup = null;
  var hoverKey = null;

  // ---------- small helpers ----------

  function esc(value) {
    return String(value === null || value === undefined ? "" : value)
      .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;").replace(/'/g, "&#39;");
  }

  // Matches AreaSlug.For on the server ("Steven's Square - Loring Heights" -> "steven-s-square-loring-heights").
  function slug(name) {
    return String(name).toLowerCase().replace(/&/g, " and ").replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "");
  }

  function parseDate(iso) {
    var p = iso.split("-").map(Number);
    return { y: p[0], m: p[1], d: p[2] };
  }

  function shortDate(iso, withYear) {
    var p = parseDate(iso);
    return MONTHS[p.m - 1] + " " + p.d + (withYear ? ", " + p.y : "");
  }

  function rangeLabel(from, to) {
    var a = parseDate(from), b = parseDate(to);
    return a.y === b.y ? shortDate(from, false) + " to " + shortDate(to, true) : shortDate(from, true) + " to " + shortDate(to, true);
  }

  // ---------- state <-> controls <-> URL ----------

  var groupSelect = document.getElementById("map-group");
  var fromInput = document.getElementById("from-date");
  var toInput = document.getElementById("to-date");

  function readUrl() {
    var p = new URLSearchParams(location.search);
    if (p.get("group")) state.group = p.get("group");
    // Only the periods the toolbar offers; anything else (an old shared link, say) falls back to the default.
    if (["7", "30", "90", "365", "ytd"].indexOf(p.get("range")) !== -1) state.range = p.get("range");
    if (p.get("from") && p.get("to")) { state.from = p.get("from"); state.to = p.get("to"); state.range = "custom"; }
    if (p.get("mode") === "heat") state.mode = "heat";
    if (p.get("residents") === "0") state.residents = false;
  }

  function syncUrl() {
    var p = new URLSearchParams();
    if (state.group !== "car") p.set("group", state.group);
    if (state.range === "custom" && state.from && state.to) { p.set("from", state.from); p.set("to", state.to); }
    else if (state.range !== "30") p.set("range", state.range);
    if (state.mode === "heat") p.set("mode", "heat");
    if (!state.residents) p.set("residents", "0");
    var qs = p.toString();
    history.replaceState(null, "", location.pathname + (qs ? "?" + qs : ""));
  }

  function applyStateToControls() {
    groupSelect.value = state.group;
    if (groupSelect.value !== state.group) { state.group = "car"; groupSelect.value = "car"; }
    document.querySelectorAll("#date-presets [data-range]").forEach(function (b) {
      var on = b.dataset.range === state.range;
      b.classList.toggle("active", on);
      b.setAttribute("aria-pressed", on ? "true" : "false");
    });
    var custom = state.range === "custom";
    fromInput.hidden = !custom;
    toInput.hidden = !custom;
    if (custom) { fromInput.value = state.from; toInput.value = state.to; }
    document.getElementById("mode-clusters").classList.toggle("active", state.mode === "clusters");
    document.getElementById("mode-heat").classList.toggle("active", state.mode === "heat");
    document.getElementById("layer-residents").checked = state.residents;
  }

  function query() {
    var p = new URLSearchParams();
    p.set("group", state.group);
    if (state.range === "custom") { p.set("from", state.from); p.set("to", state.to); }
    else p.set("range", state.range);
    return p.toString();
  }

  // ---------- the map ----------

  if (isMapbox) GL.accessToken = cfg.token;

  var mapOptions = {
    container: "map",
    style: isMapbox ? cfg.style : "https://tiles.openfreemap.org/styles/liberty",
    center: [-93.265, 44.978],
    zoom: 11.2,
    minZoom: 9,
    maxZoom: 18.5,
    maxBounds: [[-93.6, 44.78], [-92.9, 45.17]],
    attributionControl: true
  };
  if (isMapbox) {
    mapOptions.projection = "mercator";
    mapOptions.customAttribution = "MPD open data";
  }

  var map = new GL.Map(mapOptions);
  window.__gotsMap = map; // handy in the console and for tests

  map.addControl(new GL.NavigationControl({ showCompass: false }), "top-right");
  map.addControl(new GL.FullscreenControl({ container: frame }), "top-right");
  map.addControl(new GL.GeolocateControl({ positionOptions: { enableHighAccuracy: false }, trackUserLocation: false }), "top-right");
  map.addControl(new GL.ScaleControl({ maxWidth: 90, unit: "imperial" }), "bottom-right");

  // Mapbox's styles ship their own glyphs; OpenFreeMap's use Noto Sans.
  function textFont() {
    if (isMapbox) return ["DIN Offc Pro Medium", "Arial Unicode MS Bold"];
    try {
      var layer = map.getStyle().layers.filter(function (l) {
        return l.type === "symbol" && l.layout && Array.isArray(l["layout"]["text-font"]);
      })[0];
      if (layer) return layer.layout["text-font"];
    } catch (e) { /* use the default below */ }
    return ["Noto Sans Bold"];
  }

  // Scales follow the data, so a quiet month and an all-time view both use the whole ramp.
  function blockStops(max) {
    var m = Math.max(max, 6);
    return [1, 1 + (m - 1) * 0.1, 1 + (m - 1) * 0.35, 1 + (m - 1) * 0.65, m];
  }

  function clusterThresholds(total) {
    var t1 = Math.max(10, total * 0.01);
    var t2 = Math.max(t1 * 3, total * 0.05);
    var t3 = Math.max(t2 * 3, total * 0.15);
    return [t1, t2, t3];
  }

  function interpolateOnN(stops, values) {
    var expr = ["interpolate", ["linear"], ["get", "n"]];
    for (var i = 0; i < stops.length; i++) expr.push(stops[i], values[i]);
    return expr;
  }

  function updateScales(blocksMeta) {
    var max = blocksMeta.max || 1;
    var stops = blockStops(max);
    var t = clusterThresholds(blocksMeta.total || 1);

    map.setPaintProperty("blocks-points", "circle-color", interpolateOnN(stops, RAMP));
    map.setPaintProperty("blocks-points", "circle-radius", interpolateOnN(stops, RADII));
    map.setPaintProperty("clusters", "circle-color", ["step", ["get", "sum"], RAMP[1], t[0], RAMP[2], t[1], RAMP[3], t[2], RAMP[4]]);
    map.setPaintProperty("clusters", "circle-radius", ["step", ["get", "sum"], 16, t[0], 20, t[1], 26, t[2], 34]);
    map.setPaintProperty("cluster-count", "text-color", ["step", ["get", "sum"], INK, t[0], INK, t[1], "#ffffff", t[2], "#ffffff"]);
    // Square-root weight: blocks are heavily skewed, and a straight scale leaves most of the city blank.
    map.setPaintProperty("blocks-heat", "heatmap-weight", ["interpolate", ["linear"], ["sqrt", ["get", "n"]], 0, 0, Math.sqrt(max), 1]);
    document.getElementById("mlc-max").textContent = fmt(max);
  }

  function addLayers() {
    map.addSource("blocks", {
      type: "geojson",
      data: EMPTY,
      cluster: true,
      clusterRadius: 46,
      clusterMaxZoom: 13,
      clusterProperties: { sum: ["+", ["get", "n"]] }
    });
    // The heatmap reads the same blocks without clustering.
    map.addSource("blocks-raw", { type: "geojson", data: EMPTY });
    map.addSource("residents", { type: "geojson", data: EMPTY });

    map.addLayer({
      id: "blocks-heat",
      type: "heatmap",
      source: "blocks-raw",
      layout: { visibility: "none" },
      paint: {
        "heatmap-weight": 1,
        "heatmap-intensity": ["interpolate", ["linear"], ["zoom"], 9, 0.35, 13, 0.9],
        "heatmap-radius": ["interpolate", ["linear"], ["zoom"], 9, 12, 12, 24, 15, 56],
        "heatmap-opacity": 0.85,
        "heatmap-color": [
          "interpolate", ["linear"], ["heatmap-density"],
          0, "rgba(253,231,211,0)",
          0.1, "rgba(248,183,136,0.45)",
          0.35, "rgba(240,138,69,0.8)",
          0.65, "rgba(234,88,12,0.9)",
          1, "rgba(154,52,18,0.95)"
        ]
      }
    });

    map.addLayer({
      id: "clusters",
      type: "circle",
      source: "blocks",
      filter: ["has", "point_count"],
      paint: {
        "circle-color": RAMP[2],
        "circle-radius": 18,
        "circle-opacity": 0.92,
        "circle-stroke-width": 2,
        "circle-stroke-color": "#ffffff"
      }
    });

    map.addLayer({
      id: "cluster-count",
      type: "symbol",
      source: "blocks",
      filter: ["has", "point_count"],
      layout: {
        "text-field": ["number-format", ["get", "sum"], {}],
        "text-font": textFont(),
        "text-size": 12,
        "text-allow-overlap": true
      },
      paint: { "text-color": INK }
    });

    map.addLayer({
      id: "blocks-points",
      type: "circle",
      source: "blocks",
      filter: ["!", ["has", "point_count"]],
      paint: {
        "circle-color": RAMP[3],
        "circle-radius": 6,
        "circle-opacity": 0.9,
        "circle-stroke-width": 1.5,
        "circle-stroke-color": "#ffffff"
      }
    });

    map.addLayer({
      id: "residents",
      type: "circle",
      source: "residents",
      paint: {
        "circle-radius": 7,
        "circle-color": ["match", ["get", "incidentType"], "WindowSmashed", RESIDENT.WindowSmashed, "Rifled", RESIDENT.Rifled, INK],
        "circle-stroke-width": 2.5,
        "circle-stroke-color": "#ffffff"
      }
    });
  }

  // ---------- cards (hover and click) ----------

  function parseTop(value) {
    return String(value || "").split("|").filter(Boolean).map(function (pair) {
      var i = pair.lastIndexOf(":");
      var key = pair.slice(0, i);
      return { key: key, label: (meta && meta.groupLabels[key]) || key, count: Number(pair.slice(i + 1)) };
    });
  }

  function barsHtml(rows) {
    if (!rows.length) return "";
    var max = Math.max.apply(null, rows.map(function (r) { return r.count; })) || 1;
    return '<ul class="mp-bars">' + rows.map(function (r) {
      return '<li><span class="mp-bar-label">' + esc(r.label) + '</span>' +
        '<span class="mp-bar-track"><span class="mp-bar" style="width:' + (r.count / max * 100) + '%"></span></span>' +
        '<span class="mp-bar-count">' + fmt(r.count) + '</span></li>';
    }).join("") + "</ul>";
  }

  function periodText() {
    return meta ? rangeLabel(meta.from, meta.to) : "";
  }

  function blockCard(p, withLink) {
    var place = [p.h, Number(p.w) > 0 ? "Ward " + p.w : null].filter(Boolean).map(esc).join(" &middot; ");
    var n = Number(p.n);
    var html = '<div class="mp-title">' + esc(p.a || "Block") + "</div>" +
      (place ? '<div class="mp-sub">' + place + "</div>" : "") +
      '<div class="mp-big">' + fmt(n) + " <span>offense" + (n === 1 ? "" : "s") + "</span></div>" +
      barsHtml(parseTop(p.t)) +
      '<div class="mp-foot">' + esc(periodText()) + "</div>";
    if (withLink && p.h) {
      html += '<div class="mp-links"><a href="/neighborhoods/' + esc(slug(p.h)) + '">Open ' + esc(p.h) + " page</a></div>";
    }
    return html;
  }

  function clusterCard(p, extra) {
    var blocks = Number(p.point_count);
    var html = '<div class="mp-big">' + fmt(Number(p.sum)) + " <span>offenses</span></div>" +
      '<div class="mp-sub">across ' + fmt(blocks) + " blocks</div>";
    if (extra) html += extra;
    return html + '<div class="mp-foot">' + esc(periodText()) + " &middot; click to zoom in</div>";
  }

  // getClusterLeaves/getClusterExpansionZoom take a callback in some builds and return a promise in
  // others; this works with both.
  function viaCallbackOrPromise(call) {
    return new Promise(function (resolve, reject) {
      var done = false;
      function finish(err, value) {
        if (done) return;
        done = true;
        if (err) reject(err); else resolve(value);
      }
      try {
        var result = call(finish);
        if (result && typeof result.then === "function") result.then(function (v) { finish(null, v); }, function (e) { finish(e); });
      } catch (e) { finish(e); }
    });
  }

  function clusterDetails(clusterId, count) {
    if (count > CLUSTER_DETAIL_LIMIT) return Promise.resolve("");
    var source = map.getSource("blocks");
    return viaCallbackOrPromise(function (cb) { return source.getClusterLeaves(clusterId, CLUSTER_DETAIL_LIMIT, 0, cb); })
      .then(function (leaves) {
        var hoods = {}, types = {};
        leaves.forEach(function (leaf) {
          var p = leaf.properties;
          if (p.h) hoods[p.h] = (hoods[p.h] || 0) + Number(p.n);
          parseTop(p.t).forEach(function (t) { types[t.label] = (types[t.label] || 0) + t.count; });
        });
        function top(obj, k) {
          return Object.keys(obj).map(function (name) { return { label: name, count: obj[name] }; })
            .sort(function (a, b) { return b.count - a.count; }).slice(0, k);
        }
        var a = top(hoods, 3), b = top(types, 3);
        return (a.length ? '<div class="mp-h">Most in</div>' + barsHtml(a) : "") + (b.length ? '<div class="mp-h">Mostly</div>' + barsHtml(b) : "");
      })
      .catch(function () { return ""; });
  }

  function residentCard(p, withFlag) {
    var label = p.incidentType === "WindowSmashed" ? "Window smashed" : p.incidentType === "Rifled" ? "Rifled through" : "Resident report";
    var timeLabel = p.timeOfDay ? String(p.timeOfDay).replace(/([a-z])([A-Z])/g, "$1 $2") : null;
    var parts = [timeLabel, p.itemsStolen ? "items taken" : null, p.policeReported ? "reported to police" : null].filter(Boolean).join(" &middot; ");
    return '<div class="mp-title">' + esc(label) + '</div>' +
      '<div class="mp-sub">' + esc(p.reportedDate) + (parts ? " &middot; " + parts : "") + "</div>" +
      (p.crossStreets ? '<div class="mp-sub">' + esc(p.crossStreets) + "</div>" : "") +
      (p.neighborhood ? '<div class="mp-sub">' + esc(p.neighborhood) + "</div>" : "") +
      '<div class="mp-sub">Resident report, not verified</div>' +
      (withFlag ? '<button type="button" class="btn-toggle mp-flag" data-flag-id="' + esc(p.id) + '">Flag as wrong or spam</button>' : "");
  }

  // Cards stay narrower than the map, so a tap card never runs off a phone screen.
  function popupWidth(max) {
    return Math.max(220, Math.min(max, frame.clientWidth - 36)) + "px";
  }

  function hideHover() {
    hoverKey = null;
    if (hoverPopup) { hoverPopup.remove(); hoverPopup = null; }
  }

  function showHover(layer, feature) {
    var coords = feature.geometry.coordinates.slice();
    var p = feature.properties;
    var key = layer + ":" + (p.cluster_id !== undefined ? p.cluster_id : coords.join(","));
    if (key === hoverKey) return;
    hideHover();
    hoverKey = key;

    var html = layer === "clusters" ? clusterCard(p, "") : layer === "residents" ? residentCard(p, false) : blockCard(p, false);
    hoverPopup = new GL.Popup({ closeButton: false, closeOnClick: false, closeOnMove: false, offset: 16, maxWidth: popupWidth(300), className: "map-hover" })
      .setLngLat(coords)
      .setHTML(html)
      .addTo(map);

    if (layer === "clusters") {
      var mine = hoverKey;
      clusterDetails(p.cluster_id, Number(p.point_count)).then(function (extra) {
        if (hoverKey === mine && hoverPopup && extra) hoverPopup.setHTML(clusterCard(p, extra));
      });
    }
  }

  function wireFlagButton(root) {
    var btn = root.querySelector("[data-flag-id]");
    if (!btn) return;
    btn.addEventListener("click", function (evt) {
      var target = evt.target;
      var id = target.getAttribute("data-flag-id");
      target.disabled = true;
      target.textContent = "Flagged";
      fetch("/api/reports/" + encodeURIComponent(id) + "/flag", {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({})
      }).catch(function () {
        target.textContent = "Could not flag. Try again";
        target.disabled = false;
      });
    });
  }

  // On a phone-width map a floating card can't be placed reliably, so taps open a bottom sheet instead.
  var narrow = window.matchMedia("(max-width: 640px)");
  var sheet = document.getElementById("map-sheet");

  function closeSheet() {
    sheet.hidden = true;
    sheet.textContent = "";
  }

  function openSheet(html, withFlag) {
    sheet.innerHTML = '<button type="button" class="mp-close" aria-label="Close">&times;</button>' + html;
    sheet.hidden = false;
    sheet.querySelector(".mp-close").addEventListener("click", closeSheet);
    if (withFlag) wireFlagButton(sheet);
  }

  function openSticky(coords, html, withFlag) {
    hideHover();
    if (stickyPopup) { stickyPopup.remove(); stickyPopup = null; }
    if (narrow.matches) {
      openSheet(html, withFlag);
      return;
    }
    stickyPopup = new GL.Popup({ closeButton: true, closeOnClick: true, offset: 14, maxWidth: popupWidth(320), className: "map-sticky" })
      .setLngLat(coords)
      .setHTML(html)
      .addTo(map);
    if (withFlag) wireFlagButton(stickyPopup.getElement());
  }

  function wireInteractions() {
    ["clusters", "blocks-points", "residents"].forEach(function (layer) {
      map.on("mousemove", layer, function (e) {
        if (!e.features || !e.features.length) return;
        map.getCanvas().style.cursor = "pointer";
        showHover(layer, e.features[0]);
      });
      map.on("mouseleave", layer, function () {
        map.getCanvas().style.cursor = "";
        hideHover();
      });
    });

    map.on("click", function (e) {
      var hit = map.queryRenderedFeatures(e.point, { layers: ["clusters", "blocks-points", "residents"] });
      if (!hit.length) closeSheet();
    });

    map.on("click", "clusters", function (e) {
      var f = e.features[0];
      var coords = f.geometry.coordinates.slice();
      var source = map.getSource("blocks");
      viaCallbackOrPromise(function (cb) { return source.getClusterExpansionZoom(f.properties.cluster_id, cb); })
        .then(function (zoom) { map.easeTo({ center: coords, zoom: Math.min(zoom + 0.3, 17) }); })
        .catch(function () { map.easeTo({ center: coords, zoom: map.getZoom() + 2 }); });
    });

    map.on("click", "blocks-points", function (e) {
      var f = e.features[0];
      openSticky(f.geometry.coordinates.slice(), blockCard(f.properties, true), false);
    });

    map.on("click", "residents", function (e) {
      var f = e.features[0];
      openSticky(f.geometry.coordinates.slice(), residentCard(f.properties, true), true);
    });
  }

  // ---------- data ----------

  // Several resident reports can share one block midpoint. Spread each stack into a small spiral,
  // kept well inside a block, so every report stays visible and clickable.
  var GOLDEN_ANGLE = 2.399963229728653;
  var METERS_PER_DEGREE_LAT = 111320;

  function spreadOverlappingPoints(geojson) {
    var groups = new Map();
    geojson.features.forEach(function (feature) {
      var key = feature.geometry.coordinates.join(",");
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key).push(feature);
    });
    groups.forEach(function (group) {
      if (group.length === 1) return;
      var lng0 = group[0].geometry.coordinates[0], lat0 = group[0].geometry.coordinates[1];
      var perDegreeLng = METERS_PER_DEGREE_LAT * Math.cos(lat0 * Math.PI / 180);
      group.forEach(function (feature, i) {
        var radius = Math.min(60, 10 * Math.sqrt(i + 1));
        var angle = i * GOLDEN_ANGLE;
        feature.geometry.coordinates = [lng0 + radius * Math.sin(angle) / perDegreeLng, lat0 + radius * Math.cos(angle) / METERS_PER_DEGREE_LAT];
      });
    });
    return geojson;
  }

  function scopeNoun() {
    if (state.group === "car") return "car-related offenses";
    if (state.group === "all") return "offenses, all crimes";
    var option = groupSelect.options[groupSelect.selectedIndex];
    return (option ? option.textContent.toLowerCase() : "") + " offenses";
  }

  function periodWords() {
    return { "7": "last 7 days", "30": "last 30 days", "90": "last 90 days", "365": "last 12 months", ytd: "so far this year", custom: "in range" }[state.range] || "";
  }

  function renderRows(el, rows, linked) {
    if (!el) return;
    if (!rows.length) { el.innerHTML = '<p class="field-hint">Nothing recorded in this period.</p>'; return; }
    var max = Math.max.apply(null, rows.map(function (r) { return r.count; })) || 1;
    el.innerHTML = rows.map(function (r) {
      var label = linked && r.url ? '<a href="' + esc(r.url) + '">' + esc(r.name) + "</a>" : esc(r.name);
      return '<div class="breakdown-row"><span class="breakdown-label">' + label + "</span>" +
        '<span class="breakdown-bar-track"><span class="breakdown-bar" style="width:' + (r.count / max * 100) + '%"></span></span>' +
        '<span class="breakdown-count">' + fmt(r.count) + "</span></div>";
    }).join("");
  }

  function renderSummary(s) {
    document.getElementById("stat-count").textContent = fmt(s.total);
    document.getElementById("stat-count-label").textContent = scopeNoun() + ", " + periodWords();
    var change = document.getElementById("stat-change");
    change.classList.remove("up", "down");
    if (s.changeVsPrior === null || s.changeVsPrior === undefined) {
      change.textContent = "—";
    } else {
      change.textContent = (s.changeVsPrior > 0 ? "+" : "") + s.changeVsPrior + "%";
      if (s.changeVsPrior > 0) change.classList.add("up");
      else if (s.changeVsPrior < 0) change.classList.add("down");
    }
    renderRows(document.getElementById("breakdown-neighborhoods"), s.neighborhoods, true);
    renderRows(document.getElementById("breakdown-time-of-day"), s.timeOfDay, false);
    renderRows(document.getElementById("breakdown-wards"), s.wards, true);
    renderRows(document.getElementById("breakdown-types"), s.types, false);
  }

  var baseStatus = "";

  function setStatus(text) {
    baseStatus = text;
    document.getElementById("map-status").textContent = text;
  }

  // Resident reports sit beside the MPD total, never inside it: a resident who also called the
  // police would be in both, and the two can't be matched.
  function showResidentCount(count) {
    var extra = count > 0
      ? " \u00b7 plus " + fmt(count) + " resident report" + (count === 1 ? "" : "s") + ", counted separately"
      : "";
    document.getElementById("map-status").textContent = baseStatus + extra;
  }

  function loadResidents() {
    if (!state.residents) { showResidentCount(0); return Promise.resolve(); }
    var p = new URLSearchParams({ source: "resident", from: meta.from });
    if (state.range === "custom") p.set("to", meta.to);
    return fetch("/api/reports?" + p.toString())
      .then(function (r) { return r.ok ? r.json() : EMPTY; })
      .then(function (geojson) {
        map.getSource("residents").setData(spreadOverlappingPoints(geojson));
        showResidentCount(geojson.features.length);
      })
      .catch(function () { /* the block layer is the main event */ });
  }

  function refresh() {
    if (state.range === "custom" && !(state.from && state.to)) return;
    var id = ++requestId;
    var qs = query();
    frame.classList.add("is-loading");
    hideHover();
    if (stickyPopup) { stickyPopup.remove(); stickyPopup = null; } // its numbers are about to be stale
    closeSheet();
    syncUrl();

    Promise.all([
      fetch("/api/map/blocks?" + qs).then(function (r) { if (!r.ok) throw new Error("blocks " + r.status); return r.json(); }),
      fetch("/api/map/summary?" + qs).then(function (r) { if (!r.ok) throw new Error("summary " + r.status); return r.json(); })
    ]).then(function (results) {
      if (id !== requestId) return;
      var geojson = results[0];
      meta = geojson.meta;
      updateScales(meta);
      map.getSource("blocks").setData(geojson);
      map.getSource("blocks-raw").setData(geojson);
      renderSummary(results[1]);
      setStatus(fmt(meta.total) + " offenses on " + fmt(meta.blocks) + " blocks, " + rangeLabel(meta.from, meta.to) +
        (meta.unlocated > 0 ? ". " + fmt(meta.unlocated) + " more have no location in MPD's data, so they are counted but not drawn." : ""));
      return loadResidents();
    }).catch(function () {
      if (id === requestId) setStatus("The map data couldn't load. Try refreshing the page.");
    }).then(function () {
      if (id === requestId) frame.classList.remove("is-loading");
    });
  }

  function setMode(mode) {
    state.mode = mode;
    var clusters = mode === "clusters";
    ["clusters", "cluster-count", "blocks-points"].forEach(function (layer) {
      map.setLayoutProperty(layer, "visibility", clusters ? "visible" : "none");
    });
    map.setLayoutProperty("blocks-heat", "visibility", clusters ? "none" : "visible");
    hideHover();
    applyStateToControls();
    syncUrl();
  }

  function wireControls() {
    groupSelect.addEventListener("change", function () { state.group = groupSelect.value; refresh(); });

    document.querySelectorAll("#date-presets [data-range]").forEach(function (button) {
      button.addEventListener("click", function () {
        state.range = button.dataset.range;
        applyStateToControls();
        if (state.range !== "custom") refresh();
      });
    });

    [fromInput, toInput].forEach(function (input) {
      input.addEventListener("change", function () {
        if (fromInput.value && toInput.value) {
          state.from = fromInput.value;
          state.to = toInput.value;
          refresh();
        }
      });
    });

    document.getElementById("mode-clusters").addEventListener("click", function () { setMode("clusters"); });
    document.getElementById("mode-heat").addEventListener("click", function () { setMode("heat"); });

    document.getElementById("layer-residents").addEventListener("change", function (e) {
      state.residents = e.target.checked;
      map.setLayoutProperty("residents", "visibility", state.residents ? "visible" : "none");
      if (meta) loadResidents();
      syncUrl();
    });
  }

  readUrl();
  applyStateToControls();
  wireControls();

  map.on("load", function () {
    if (!isMapbox && typeof retintMapLibreBasemap === "function") retintMapLibreBasemap(map);
    addLayers();
    wireInteractions();
    setMode(state.mode);
    map.setLayoutProperty("residents", "visibility", state.residents ? "visible" : "none");
    refresh();
  });

  map.on("error", function (e) {
    // A bad or URL-restricted Mapbox token surfaces here; say so instead of showing a blank map.
    var status = e && e.error && e.error.status;
    if (isMapbox && (status === 401 || status === 403)) {
      setStatus(status === 401
        ? "The map couldn't load: Mapbox rejected the access token."
        : "The map couldn't load: Mapbox refused this site. Check the token's allowed URLs.");
    }
  });
})();
