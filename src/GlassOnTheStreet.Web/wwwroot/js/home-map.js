(function () {
  const container = document.getElementById("home-map");
  if (!container) return;

  const PALETTE = {
    windowSmashed: "#ea580c",
    rifled: "#18181b",
    unknown: "#a1a1aa",
    vehicleStolen: "#3b6ea5",
    partsTheft: "#7c3aed",
    propertyDamage: "#ca8a04"
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
          "VehicleStolen", PALETTE.vehicleStolen,
          "PartsTheft", PALETTE.partsTheft,
          "PropertyDamage", PALETTE.propertyDamage,
          PALETTE.unknown
        ],
        "circle-opacity": 0.85
      }
    });

    try {
      // Local date parts, not toISOString() (UTC) -- same fix as map.js's
      // toDateInput, avoids shifting the cutoff by a day depending on the
      // viewer's timezone and time of day.
      const from = new Date();
      from.setDate(from.getDate() - 6);
      const year = from.getFullYear();
      const month = String(from.getMonth() + 1).padStart(2, "0");
      const day = String(from.getDate()).padStart(2, "0");

      const res = await fetch(`/api/reports?from=${year}-${month}-${day}`);
      if (res.ok) {
        const geojson = await res.json();
        map.getSource("home-reports").setData(geojson);
      }
    } catch (e) {
      // A dead preview map isn't worth failing the page over.
    }
  });
})();
