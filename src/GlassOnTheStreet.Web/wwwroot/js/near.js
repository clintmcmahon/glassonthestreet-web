// /near: address or device location -> counts of MPD offenses within a radius.
// The point is POSTed (never put in the URL) and nothing is stored.
(function () {
  var form = document.getElementById("near-form");
  if (!form) return;

  var addressInput = document.getElementById("near-address");
  var radiusSelect = document.getElementById("near-radius");
  var daysSelect = document.getElementById("near-days");
  var results = document.getElementById("near-results");
  var errorBox = document.getElementById("near-error");
  var fmt = function (n) { return formatNumber(n); };

  // Kept in memory only, so changing the distance or period can rerun the lookup.
  var last = null;

  function el(tag, className, text, parent) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = text;
    if (parent) parent.appendChild(node);
    return node;
  }

  function showError(message) {
    errorBox.textContent = message;
    errorBox.hidden = !message;
    if (message) results.textContent = "";
  }

  function post(url, body) {
    return fetch(url, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) })
      .then(function (r) {
        return r.json().catch(function () { return {}; }).then(function (json) {
          if (!r.ok) throw new Error(json.error || (r.status === 429 ? "Too many lookups. Wait a minute and try again." : "Something went wrong. Try again."));
          return json;
        });
      });
  }

  function pct(now, then) {
    if (!then) return "";
    var p = Math.round((now - then) / then * 100);
    return p === 0 ? "no change" : (p > 0 ? "up " : "down ") + Math.abs(p) + "%";
  }

  var RADIUS_WORDS = { "0.25": "a quarter mile", "0.5": "half a mile", "1": "a mile" };
  function periodWords(days) { return days === 365 ? "12 months" : days + " days"; }

  function render(data, label) {
    showError("");
    results.textContent = "";

    var box = el("section", "near-summary", null, results);
    var headline = el("p", "near-headline", null, box);
    headline.appendChild(document.createTextNode("Within " + RADIUS_WORDS[String(data.radiusMiles)] + " of " + label + ", MPD recorded "));
    el("strong", null, fmt(data.total) + " offenses", headline);
    headline.appendChild(document.createTextNode(" in the last " + periodWords(data.days)));
    var change = pct(data.total, data.previousTotal);
    headline.appendChild(document.createTextNode(change ? " (" + change + " from the " + periodWords(data.days) + " before)." : "."));

    if (data.neighborhood) {
      var where = el("p", "near-where", "The closest block data is in ", box);
      var link = el("a", null, data.neighborhood, where);
      link.href = data.neighborhoodUrl;
      where.appendChild(document.createTextNode(data.ward ? " (" : "."));
      if (data.ward) {
        var w = el("a", null, "Ward " + data.ward, where);
        w.href = "/wards/" + data.ward;
        where.appendChild(document.createTextNode(")."));
      }
    }

    if (!data.groups.length) {
      el("p", null, "Nothing recorded in this area for that period.", results);
    } else {
      var table = el("table", "area-table groups-table", null, results);
      var head = el("tr", null, null, el("thead", null, null, table));
      ["Type", "Last " + periodWords(data.days), "Before that", "Change"].forEach(function (h) { el("th", null, h, head); });
      var body = el("tbody", null, null, table);
      var shownMetricsHeader = false;
      data.groups.forEach(function (g) {
        if (!g.isCrime && !shownMetricsHeader) {
          shownMetricsHeader = true;
          var sep = el("tr", "near-sep", null, body);
          var cell = el("td", null, "Not counted as crimes", sep);
          cell.colSpan = 4;
        }
        var tr = el("tr", null, null, body);
        el("td", null, g.label, tr);
        el("td", null, fmt(g.count), tr);
        el("td", null, fmt(g.previous), tr);
        el("td", null, pct(g.count, g.previous), tr);
      });
    }

    if (data.recent.length) {
      el("h2", "method-h", "Most recent", results);
      var list = el("ul", "near-recent", null, results);
      data.recent.forEach(function (r) {
        var li = el("li", null, null, list);
        el("span", "near-date", r.date, li);
        // The plain-language type; MPD's own name only where the type is a catch-all.
        el("span", null, /^Other/.test(r.label) ? r.label + ": " + r.offense : r.label, li);
        if (r.block) el("span", "near-block", r.block, li);
      });
    }
  }

  function lookup(lat, lng, label) {
    last = { lat: lat, lng: lng, label: label };
    showError("");
    results.textContent = "Looking up...";
    return post("/api/near", { lat: lat, lng: lng, radiusMiles: Number(radiusSelect.value), days: Number(daysSelect.value) })
      .then(function (data) { render(data, label); })
      .catch(function (e) { showError(e.message); });
  }

  form.addEventListener("submit", function (e) {
    e.preventDefault();
    var q = addressInput.value.trim();
    if (q.length < 4) { showError("Enter a street address."); return; }
    results.textContent = "Looking up...";
    post("/api/geocode/lookup", { q: q })
      .then(function (g) { return lookup(g.lat, g.lng, q); })
      .catch(function (err) { showError(err.message); });
  });

  document.getElementById("near-locate").addEventListener("click", function () {
    if (!navigator.geolocation) { showError("Your browser can't share a location. Type an address instead."); return; }
    results.textContent = "Finding your location...";
    navigator.geolocation.getCurrentPosition(
      function (pos) { lookup(pos.coords.latitude, pos.coords.longitude, "your location"); },
      function () { showError("We couldn't get your location. Type an address instead."); },
      { enableHighAccuracy: false, timeout: 10000, maximumAge: 60000 });
  });

  [radiusSelect, daysSelect].forEach(function (select) {
    select.addEventListener("change", function () { if (last) lookup(last.lat, last.lng, last.label); });
  });
})();
