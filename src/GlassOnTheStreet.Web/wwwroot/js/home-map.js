// The homepage's map preview: a small, non-interactive look at the last week of car-related MPD
// offenses, one circle per block, in the same style as /map. It uses Mapbox when a token is
// configured (window.GOTS_MAP.engine === "mapbox") and MapLibre otherwise, and it only starts the
// map once it is about to scroll into view, so a visitor who never reaches it never starts a (billed)
// Mapbox map load. A dead preview isn't worth failing the page over, so every step is best effort.
(function () {
  "use strict";

  var container = document.getElementById("home-map");
  if (!container) return;

  var cfg = window.GOTS_MAP || { engine: "maplibre" };
  var isMapbox = cfg.engine === "mapbox";
  var GL = isMapbox ? window.mapboxgl : window.maplibregl;
  if (!GL) return;

  // The same orange ramp as /map.
  var RAMP = ["#fde7d3", "#f8b788", "#f08a45", "#ea580c", "#9a3412"];
  var RADII = [3.5, 5, 7, 10, 14];

  function blockStops(max) {
    var m = Math.max(max, 6);
    return [1, 1 + (m - 1) * 0.1, 1 + (m - 1) * 0.35, 1 + (m - 1) * 0.65, m];
  }

  function interpolateOnN(stops, values) {
    var expr = ["interpolate", ["linear"], ["get", "n"]];
    for (var i = 0; i < stops.length; i++) expr.push(stops[i], values[i]);
    return expr;
  }

  var started = false;

  function start() {
    if (started) return;
    started = true;

    if (isMapbox) GL.accessToken = cfg.token;

    var options = {
      container: "home-map",
      style: isMapbox ? cfg.style : "https://tiles.openfreemap.org/styles/liberty",
      center: [-93.265, 44.978],
      zoom: 10.6,
      interactive: false,
      attributionControl: { compact: true }
    };
    if (isMapbox) options.projection = "mercator";
    var map = new GL.Map(options);

    map.on("load", function () {
      if (!isMapbox && typeof retintMapLibreBasemap === "function") retintMapLibreBasemap(map);

      map.addSource("home-blocks", { type: "geojson", data: { type: "FeatureCollection", features: [] } });
      map.addLayer({
        id: "home-blocks-circles",
        type: "circle",
        source: "home-blocks",
        paint: {
          "circle-color": RAMP[3],
          "circle-radius": 5,
          "circle-opacity": 0.88,
          "circle-stroke-width": 1,
          "circle-stroke-color": "#ffffff"
        }
      });

      fetch("/api/map/blocks?group=car&range=7")
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (geojson) {
          if (!geojson) return;
          var stops = blockStops(geojson.meta.max || 1);
          map.setPaintProperty("home-blocks-circles", "circle-color", interpolateOnN(stops, RAMP));
          map.setPaintProperty("home-blocks-circles", "circle-radius", interpolateOnN(stops, RADII));
          map.getSource("home-blocks").setData(geojson);
        })
        .catch(function () { /* the preview is decoration; leave it empty */ });
    });
  }

  // Start the map a screen before it is visible. Without IntersectionObserver, just start it.
  if ("IntersectionObserver" in window) {
    var observer = new IntersectionObserver(function (entries) {
      if (entries.some(function (e) { return e.isIntersecting; })) {
        observer.disconnect();
        start();
      }
    }, { rootMargin: "400px 0px" });
    observer.observe(container);
  } else {
    start();
  }
})();
