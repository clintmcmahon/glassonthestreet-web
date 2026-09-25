(function () {
  const PALETTE = {
    windowSmashed: "#a6432c",
    rifled: "#2d5c58",
    paper: "#f4f0e6",
    water: "#dcd3bd",
    ink: "#211d18"
  };

  const map = new maplibregl.Map({
    container: "map",
    style: "https://tiles.openfreemap.org/styles/liberty",
    center: [-93.265, 44.978],
    zoom: 11.3,
    attributionControl: true
  });

  map.addControl(new maplibregl.NavigationControl({ showCompass: false }), "top-right");

  let currentMode = "pins";
  let currentFrom = null;
  let currentTo = null;

  function retint() {
    // Nudge the base style's palette toward the site's newsprint/ink theme
    // without hand-rolling a full vector style. Wrapped defensively since
    // layer ids on a third-party hosted style can change.
    const tint = [
      ["background", "background-color", PALETTE.paper],
      ["water", "fill-color", PALETTE.water]
    ];
    for (const [layerId, prop, value] of tint) {
      try {
        if (map.getLayer(layerId)) {
          map.setPaintProperty(layerId, prop, value);
        }
      } catch (e) {
        // non-fatal cosmetic tweak
      }
    }
  }

  function dateRangeForPreset(days) {
    if (days === "all") {
      return { from: null, to: null };
    }
    const to = new Date();
    const from = new Date();
    from.setDate(from.getDate() - Number(days) + 1);
    return { from: toDateInput(from), to: toDateInput(to) };
  }

  function toDateInput(date) {
    return date.toISOString().slice(0, 10);
  }

  function buildQuery() {
    const params = new URLSearchParams();
    if (currentFrom) params.set("from", currentFrom);
    if (currentTo) params.set("to", currentTo);
    return params.toString();
  }

  async function refresh() {
    const qs = buildQuery();
    const [geoRes, statsRes] = await Promise.all([
      fetch(`/api/reports${qs ? "?" + qs : ""}`),
      fetch(`/api/reports/stats${qs ? "?" + qs : ""}`)
    ]);
    const geojson = await geoRes.json();
    const stats = await statsRes.json();

    const source = map.getSource("reports");
    if (source) {
      source.setData(geojson);
    }

    document.getElementById("stat-count").textContent = stats.count;
    const changeEl = document.getElementById("stat-change");
    changeEl.classList.remove("up", "down");
    if (stats.percentChange === null || stats.percentChange === undefined) {
      changeEl.textContent = "—";
    } else {
      const sign = stats.percentChange > 0 ? "+" : "";
      changeEl.textContent = `${sign}${stats.percentChange}%`;
      changeEl.classList.add(stats.percentChange > 0 ? "up" : stats.percentChange < 0 ? "down" : "");
    }
  }

  function setMode(mode) {
    currentMode = mode;
    document.getElementById("mode-pins").classList.toggle("active", mode === "pins");
    document.getElementById("mode-heat").classList.toggle("active", mode === "heat");
    if (map.getLayer("reports-pins")) {
      map.setLayoutProperty("reports-pins", "visibility", mode === "pins" ? "visible" : "none");
    }
    if (map.getLayer("reports-heat")) {
      map.setLayoutProperty("reports-heat", "visibility", mode === "heat" ? "visible" : "none");
    }
  }

  function wireToolbar() {
    document.querySelectorAll("#date-presets [data-days]").forEach((btn) => {
      btn.addEventListener("click", () => {
        document.querySelectorAll("#date-presets [data-days]").forEach((b) => b.classList.remove("active"));
        btn.classList.add("active");
        document.getElementById("from-date").style.display = "none";
        document.getElementById("to-date").style.display = "none";
        const range = dateRangeForPreset(btn.dataset.days);
        currentFrom = range.from;
        currentTo = range.to;
        document.getElementById("stat-count-label").textContent =
          btn.dataset.days === "all" ? "reports, all time" : `reports, last ${btn.dataset.days} days`;
        refresh();
      });
    });

    document.getElementById("mode-pins").addEventListener("click", () => setMode("pins"));
    document.getElementById("mode-heat").addEventListener("click", () => setMode("heat"));

    const fromInput = document.getElementById("from-date");
    const toInput = document.getElementById("to-date");
    [fromInput, toInput].forEach((input) => {
      input.addEventListener("change", () => {
        if (fromInput.value && toInput.value) {
          document.querySelectorAll("#date-presets [data-days]").forEach((b) => b.classList.remove("active"));
          currentFrom = fromInput.value;
          currentTo = toInput.value;
          document.getElementById("stat-count-label").textContent = "reports in range";
          refresh();
        }
      });
    });
  }

  map.on("load", () => {
    retint();

    map.addSource("reports", {
      type: "geojson",
      data: { type: "FeatureCollection", features: [] }
    });

    map.addLayer({
      id: "reports-heat",
      type: "heatmap",
      source: "reports",
      layout: { visibility: "none" },
      paint: {
        "heatmap-weight": 1,
        "heatmap-intensity": 1.1,
        "heatmap-radius": 22,
        "heatmap-color": [
          "interpolate", ["linear"], ["heatmap-density"],
          0, "rgba(244,240,230,0)",
          0.3, "#c98a76",
          0.6, "#a6432c",
          1, "#5c1f12"
        ]
      }
    });

    map.addLayer({
      id: "reports-pins",
      type: "circle",
      source: "reports",
      paint: {
        "circle-radius": 5.5,
        "circle-color": [
          "match", ["get", "incidentType"],
          "WindowSmashed", PALETTE.windowSmashed,
          "Rifled", PALETTE.rifled,
          PALETTE.ink
        ],
        "circle-stroke-width": 1.5,
        "circle-stroke-color": "#fff"
      }
    });

    map.on("mouseenter", "reports-pins", () => (map.getCanvas().style.cursor = "pointer"));
    map.on("mouseleave", "reports-pins", () => (map.getCanvas().style.cursor = ""));

    map.on("click", "reports-pins", (e) => {
      const feature = e.features[0];
      const p = feature.properties;
      const incidentLabel = p.incidentType === "WindowSmashed" ? "Window smashed" : "Rifled through";
      const timeLabel = p.timeOfDay ? p.timeOfDay.replace(/([a-z])([A-Z])/g, "$1 $2") : null;
      const parts = [timeLabel, p.itemsStolen ? "items taken" : null, p.policeReported ? "reported to police" : null]
        .filter(Boolean)
        .join(" · ");

      const popup = new maplibregl.Popup({ closeButton: true })
        .setLngLat(feature.geometry.coordinates)
        .setHTML(
          `<div class="popup-title">${incidentLabel}</div>` +
          `<div class="popup-meta">${p.reportedDate}${parts ? " · " + parts : ""}</div>` +
          (p.crossStreets ? `<div class="popup-meta">${p.crossStreets}</div>` : "") +
          `<button type="button" class="btn-toggle" data-flag-id="${p.id}" style="margin-top: 0.6rem; font-size: 0.78rem;">Flag as wrong or spam</button>`
        )
        .addTo(map);

      popup.getElement().querySelector("[data-flag-id]").addEventListener("click", async (evt) => {
        const id = evt.target.getAttribute("data-flag-id");
        evt.target.disabled = true;
        evt.target.textContent = "Flagged";
        try {
          await fetch(`/api/reports/${id}/flag`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({})
          });
        } catch (err) {
          evt.target.textContent = "Could not flag — try again";
          evt.target.disabled = false;
        }
      });
    });

    wireToolbar();
    const range = dateRangeForPreset("30");
    currentFrom = range.from;
    currentTo = range.to;
    refresh();
  });
})();
