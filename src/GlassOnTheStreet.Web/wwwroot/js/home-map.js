(function () {
  const container = document.getElementById("home-map");
  if (!container) return;

  const PALETTE = {
    windowSmashed: "#ea580c",
    rifled: "#18181b",
    unknown: "#a1a1aa"
  };

  const map = new maplibregl.Map({
    container: "home-map",
    style: "https://tiles.openfreemap.org/styles/liberty",
    center: [-93.265, 44.978],
    zoom: 10.6,
    interactive: false,
    attributionControl: false
  });

  map.on("load", async () => {
    retintMapLibreBasemap(map);

    map.addSource("home-reports", {
      type: "geojson",
      data: { type: "FeatureCollection", features: [] }
    });

    map.addLayer({
      id: "home-reports-pins",
      type: "circle",
      source: "home-reports",
      paint: {
        "circle-radius": 3.5,
        "circle-color": [
          "match", ["get", "incidentType"],
          "WindowSmashed", PALETTE.windowSmashed,
          "Rifled", PALETTE.rifled,
          PALETTE.unknown
        ],
        "circle-opacity": 0.85
      }
    });

    try {
      const res = await fetch("/api/reports");
      if (res.ok) {
        const geojson = await res.json();
        map.getSource("home-reports").setData(geojson);
      }
    } catch (e) {
      // A dead preview map isn't worth failing the page over.
    }
  });
})();
