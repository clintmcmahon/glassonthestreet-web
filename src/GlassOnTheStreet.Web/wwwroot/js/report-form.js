(function () {
  const map = new maplibregl.Map({
    container: "picker-map",
    style: "https://tiles.openfreemap.org/styles/liberty",
    center: [-93.265, 44.978],
    zoom: 11
  });
  map.addControl(new maplibregl.NavigationControl({ showCompass: false }), "top-right");
  map.on("load", () => retintMapLibreBasemap(map));

  let marker = null;
  let selectedLat = null;
  let selectedLng = null;

  function setLocation(lat, lng, label) {
    selectedLat = lat;
    selectedLng = lng;
    if (marker) {
      marker.remove();
    }
    marker = new maplibregl.Marker({ color: "#ff4405" }).setLngLat([lng, lat]).addTo(map);
    document.getElementById("location-status").textContent = label || `Selected: ${lat.toFixed(5)}, ${lng.toFixed(5)}`;
  }

  map.on("click", (e) => {
    setLocation(e.lngLat.lat, e.lngLat.lng);
  });

  let searchTimer = null;
  document.getElementById("address-search").addEventListener("input", (e) => {
    const query = e.target.value.trim();
    clearTimeout(searchTimer);
    if (query.length < 4) return;
    searchTimer = setTimeout(async () => {
      try {
        const res = await fetch(`/api/geocode?q=${encodeURIComponent(query)}`);
        if (!res.ok) return;
        const result = await res.json();
        map.flyTo({ center: [result.lng, result.lat], zoom: 15 });
        setLocation(result.lat, result.lng, result.displayName);
      } catch (err) {
        // silent -- user can still tap the map
      }
    }, 500);
  });

  const form = document.getElementById("report-form");
  const errorEl = document.getElementById("form-error");

  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    errorEl.style.display = "none";

    if (selectedLat === null || selectedLng === null) {
      errorEl.textContent = "Please select a location on the map or search for an address.";
      errorEl.style.display = "block";
      return;
    }

    const formData = new FormData(form);
    formData.set("Lat", selectedLat);
    formData.set("Lng", selectedLng);

    const turnstileResponse = document.querySelector('[name="cf-turnstile-response"]');
    if (turnstileResponse) {
      formData.set("CaptchaToken", turnstileResponse.value);
    }

    const submitBtn = form.querySelector("button[type=submit]");
    submitBtn.disabled = true;
    submitBtn.textContent = "Submitting…";

    try {
      const res = await fetch("/api/reports", { method: "POST", body: formData });
      if (!res.ok) {
        const body = await res.json().catch(() => ({}));
        throw new Error(body.error || "Something went wrong submitting your report.");
      }
      const result = await res.json();
      window.location.href = `/report/confirmation?showPoliceLink=${result.showPoliceLink}`;
    } catch (err) {
      errorEl.textContent = err.message;
      errorEl.style.display = "block";
      submitBtn.disabled = false;
      submitBtn.textContent = "Submit report";
    }
  });
})();
