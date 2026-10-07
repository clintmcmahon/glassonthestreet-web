// Dashboard script for /trends (car-related crime) and /crime (every offense).
// Fetches the page's API (data-api) for the current filters and draws the
// views from the one response: year-over-year lines, same-period columns, a
// month-by-year heatmap, the area rankings and, when the response carries
// them, an hour-by-weekday heatmap and an offense-group table. Charts are
// plain SVG/HTML; every label that comes from the API goes in with textContent.
(function () {
  var root = document.getElementById("trends");
  if (!root) return;

  var API = root.dataset.api || "/api/trends";
  var UNIT = root.dataset.unit || "reports";
  var ALL_PHRASE = root.dataset.allPhrase || "reports in these categories";

  var SVGNS = "http://www.w3.org/2000/svg";
  var MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
  var fmt = function (n) { return formatNumber(n); };

  var state = { category: "", area: "", areasTab: "neighborhoods", hidden: {}, sort: "" };
  var data = null;
  var tables = {}; // chart key -> { headers, rows } for the table view and CSV
  var requestId = 0;

  // ---------- small DOM helpers ----------

  function svgEl(tag, attrs, parent) {
    var node = document.createElementNS(SVGNS, tag);
    Object.keys(attrs || {}).forEach(function (k) { node.setAttribute(k, attrs[k]); });
    if (parent) parent.appendChild(node);
    return node;
  }

  function htmlEl(tag, className, text, parent) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = text;
    if (parent) parent.appendChild(node);
    return node;
  }

  function section(id) { return document.getElementById(id); }
  function canvasOf(id) { return section(id).querySelector("[data-canvas]"); }

  function niceMax(value) {
    if (value <= 0) return 10;
    var rough = value * 1.08;
    var magnitude = Math.pow(10, Math.floor(Math.log10(rough)));
    var steps = [1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10];
    for (var i = 0; i < steps.length; i++) {
      if (steps[i] * magnitude >= rough) return steps[i] * magnitude;
    }
    return 10 * magnitude;
  }

  function percentChange(now, then) {
    if (!then) return null;
    return (now - then) / then * 100;
  }

  function formatChange(pct) {
    if (pct === null) return "n/a";
    var rounded = Math.round(pct);
    if (rounded === 0) return "no change";
    return (rounded > 0 ? "up " : "down ") + Math.abs(rounded) + "%";
  }

  function arrowFor(pct) {
    if (pct === null || Math.round(pct) === 0) return "→";
    return pct > 0 ? "▲" : "▼";
  }

  function monthDayLabel(iso) {
    var p = iso.split("-");
    return MONTHS[Number(p[1]) - 1] + " " + Number(p[2]);
  }

  // ---------- year colors ----------
  // One fixed color per year, so toggling a year on or off never repaints
  // the others. The order is the validated categorical order (adjacent-pair
  // CVD and normal-vision checks pass; run scripts/validate_palette.js to
  // recheck): the baseline year is blue, the current year is the site
  // accent, and the years between take the middle slots in year order. A
  // palette is never cycled: with more than six middle years the oldest
  // ones fall back to neutral gray (the legend, tooltip and table still
  // identify them).

  var BASELINE_COLOR = "#2a78d6";
  var CURRENT_COLOR = "#ea580c";
  var MIDDLE_COLORS = ["#e34948", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#4a3aa7"];
  var OLDER_COLOR = "#b4b3ad";

  function yearColor(index, count) {
    if (index === count - 1) return CURRENT_COLOR;
    if (index === 0) return BASELINE_COLOR;
    var offset = Math.max(0, count - 2 - MIDDLE_COLORS.length);
    var slot = index - 1 - offset;
    return slot < 0 ? OLDER_COLOR : MIDDLE_COLORS[slot];
  }

  function isHidden(year) { return !!state.hidden[year]; }

  // ---------- tooltip ----------

  function tooltipFor(canvas) {
    var tip = canvas.querySelector(".trends-tooltip");
    if (!tip) {
      tip = htmlEl("div", "trends-tooltip", null, canvas);
      tip.setAttribute("role", "status");
    }
    return tip;
  }

  function placeTooltip(canvas, tip, x, y) {
    tip.classList.add("is-visible");
    var w = tip.offsetWidth, h = tip.offsetHeight;
    var left = x + 14;
    if (left + w > canvas.clientWidth) left = x - w - 14;
    if (left < 0) left = 0;
    var top = Math.min(Math.max(y - h / 2, 0), Math.max(0, canvas.clientHeight - h));
    tip.style.left = left + "px";
    tip.style.top = top + "px";
  }

  function hideTooltip(canvas) {
    var tip = canvas.querySelector(".trends-tooltip");
    if (tip) tip.classList.remove("is-visible");
  }

  // ---------- chart 1: year over year ----------

  function renderYoY() {
    var canvas = canvasOf("chart-yoy");
    var legend = section("chart-yoy").querySelector("[data-legend]");
    canvas.textContent = "";
    legend.textContent = "";

    var years = data.years;
    var n = years.length;
    var cur = n - 1;
    var throughParts = data.through.split("-").map(Number);
    var daysInThroughMonth = new Date(throughParts[0], throughParts[1], 0).getDate();
    var partialMonth = throughParts[2] < daysInThroughMonth ? throughParts[1] - 1 : -1;

    var visibleCount = years.filter(function (y) { return !isHidden(y); }).length;

    years.forEach(function (year, i) {
      var off = isHidden(year);
      var item = htmlEl("button", "trends-legend-item" + (off ? " is-off" : ""), null, legend);
      item.type = "button";
      item.setAttribute("aria-pressed", off ? "false" : "true");
      item.title = (off ? "Show " : "Hide ") + year;
      var key = htmlEl("span", "trends-legend-key", null, item);
      key.style.borderColor = yearColor(i, n);
      if (i === cur) key.classList.add("is-current");
      htmlEl("span", null, i === 0 ? year + " (pre-COVID)" : String(year), item);
      item.addEventListener("click", function () {
        // Counted now, not at render time, so a stale handler can't hide the last line.
        var visibleNow = years.filter(function (y) { return !isHidden(y); }).length;
        if (!isHidden(year) && visibleNow <= 1) return; // keep at least one line on the chart
        if (isHidden(year)) delete state.hidden[year]; else state.hidden[year] = true;
        syncUrl();
        renderYoY();
      });
    });
    if (visibleCount < n) {
      var showAll = htmlEl("button", "trends-legend-reset", "Show all years", legend);
      showAll.type = "button";
      showAll.addEventListener("click", function () { state.hidden = {}; syncUrl(); renderYoY(); });
    }

    if (partialMonth >= 0 && !isHidden(years[cur])) {
      var partialItem = htmlEl("span", "trends-legend-static", null, legend);
      htmlEl("span", "trends-legend-hollow", null, partialItem);
      htmlEl("span", null, MONTHS[partialMonth] + " " + years[cur] + " through " + monthDayLabel(data.through) + " (the newest days are still being posted)", partialItem);
    }

    var W = Math.max(canvas.clientWidth, 320), H = W < 520 ? 280 : 340;
    var m = { l: 48, r: 56, t: 12, b: 28 };
    var innerW = W - m.l - m.r, innerH = H - m.t - m.b;

    var maxValue = 0;
    data.monthly.forEach(function (series, si) {
      if (isHidden(years[si])) return;
      series.forEach(function (v) { if (v !== null && v > maxValue) maxValue = v; });
    });
    var yMax = niceMax(maxValue);

    function x(monthIndex) { return m.l + innerW * monthIndex / 11; }
    function y(value) { return m.t + innerH * (1 - value / yMax); }

    var svg = svgEl("svg", { width: W, height: H, viewBox: "0 0 " + W + " " + H, class: "trends-svg", tabindex: "0", role: "group",
      "aria-label": "Monthly " + UNIT + " for " + years[0] + " to " + years[cur] + ". Use the left and right arrow keys to move between months." }, canvas);

    var step = yMax / 4;
    for (var g = 0; g <= 4; g++) {
      var gy = y(step * g);
      svgEl("line", { x1: m.l, x2: W - m.r, y1: gy, y2: gy, class: "trends-grid" }, svg);
      var label = svgEl("text", { x: m.l - 8, y: gy + 4, class: "trends-axis", "text-anchor": "end" }, svg);
      label.textContent = fmt(step * g);
    }
    MONTHS.forEach(function (name, i) {
      var t = svgEl("text", { x: x(i), y: H - 8, class: "trends-axis", "text-anchor": "middle" }, svg);
      t.textContent = name;
    });

    // Older years first so the current year paints on top.
    for (var i = 0; i < n; i++) {
      if (isHidden(years[i])) continue;
      var values = data.monthly[i];
      var points = [];
      var lastIndex = -1;
      for (var mi = 0; mi < 12; mi++) {
        if (values[mi] === null) break;
        lastIndex = mi;
      }
      var solidEnd = (i === cur && partialMonth >= 0 && lastIndex === partialMonth) ? lastIndex - 1 : lastIndex;
      for (var k = 0; k <= solidEnd; k++) points.push(x(k) + "," + y(values[k]));
      var color = yearColor(i, n);
      if (points.length > 1) {
        svgEl("polyline", { points: points.join(" "), class: "trends-line" + (i === cur ? " is-current" : ""), style: "stroke:" + color }, svg);
      }
      // The current, still-incomplete month is a hollow marker with no line
      // into it: a slope toward a month that isn't over reads as a drop.
      if (i === cur && solidEnd < lastIndex && solidEnd >= 0) {
        svgEl("circle", { cx: x(lastIndex), cy: y(values[lastIndex]), r: 4.5, class: "trends-dot-partial", style: "stroke:" + color }, svg);
      }
      // End labels only for the baseline and the current year; the legend and tooltip carry the rest.
      if ((i === 0 || i === cur) && lastIndex >= 0) {
        var endLabel = svgEl("text", { x: x(lastIndex) + 8, y: y(values[lastIndex]) + 4, class: "trends-end-label" }, svg);
        endLabel.textContent = String(years[i]);
      }
    }

    var guide = svgEl("line", { y1: m.t, y2: m.t + innerH, class: "trends-guide", style: "display:none" }, svg);
    var dots = svgEl("g", {}, svg);
    var tip = tooltipFor(canvas);
    var activeMonth = -1;

    function showMonth(monthIndex, pointerY) {
      activeMonth = monthIndex;
      guide.setAttribute("x1", x(monthIndex));
      guide.setAttribute("x2", x(monthIndex));
      guide.style.display = "";
      dots.textContent = "";

      tip.textContent = "";
      var isPartial = monthIndex === partialMonth;
      htmlEl("div", "trends-tip-title", MONTHS[monthIndex] + (isPartial && !isHidden(years[cur]) ? " (through " + monthDayLabel(data.through) + " for " + years[cur] + ")" : ""), tip);
      for (var i = n - 1; i >= 0; i--) {
        var v = data.monthly[i][monthIndex];
        if (v === null || isHidden(years[i])) continue;
        var row = htmlEl("div", "trends-tip-row", null, tip);
        var key = htmlEl("span", "trends-tip-key", null, row);
        key.style.background = yearColor(i, n);
        htmlEl("strong", null, fmt(v), row);
        htmlEl("span", "trends-tip-label", String(years[i]), row);
        svgEl("circle", { cx: x(monthIndex), cy: y(v), r: 4, class: "trends-dot", style: "fill:" + yearColor(i, n) }, dots);
      }
      placeTooltip(canvas, tip, x(monthIndex), pointerY === undefined ? m.t + innerH / 3 : pointerY);
    }

    function clear() {
      activeMonth = -1;
      guide.style.display = "none";
      dots.textContent = "";
      hideTooltip(canvas);
    }

    svg.addEventListener("pointermove", function (e) {
      var box = svg.getBoundingClientRect();
      var px = e.clientX - box.left;
      var monthIndex = Math.round((px - m.l) / innerW * 11);
      if (monthIndex < 0 || monthIndex > 11) return clear();
      showMonth(monthIndex, e.clientY - box.top);
    });
    svg.addEventListener("pointerleave", clear);
    svg.addEventListener("blur", clear);
    svg.addEventListener("keydown", function (e) {
      if (e.key !== "ArrowLeft" && e.key !== "ArrowRight") return;
      e.preventDefault();
      var next = activeMonth < 0 ? 0 : activeMonth + (e.key === "ArrowRight" ? 1 : -1);
      showMonth(Math.min(11, Math.max(0, next)));
    });

    var headers = ["Month"].concat(years.map(String));
    var rows = MONTHS.map(function (name, mi) {
      return [name].concat(years.map(function (_, i) { return data.monthly[i][mi] === null ? "" : data.monthly[i][mi]; }));
    });
    tables.yoy = { headers: headers, rows: rows };
  }

  // ---------- chart 2: same period ----------

  function renderPeriod() {
    var canvas = canvasOf("chart-period");
    var stats = section("chart-period").querySelector("[data-stats]");
    canvas.textContent = "";
    stats.textContent = "";

    var years = data.years, counts = data.samePeriod;
    var n = years.length, cur = n - 1;
    var windowLabel = "Jan 1 to " + monthDayLabel(data.through);

    var comparisons = [
      { label: "vs " + years[cur - 1], pct: percentChange(counts[cur], counts[cur - 1]) },
      { label: "vs " + years[0] + " (pre-COVID)", pct: percentChange(counts[cur], counts[0]) }
    ];
    comparisons.forEach(function (c) {
      var tile = htmlEl("div", "trends-stat", null, stats);
      htmlEl("div", "trends-stat-label", c.label, tile);
      var value = htmlEl("div", "trends-stat-value", null, tile);
      htmlEl("span", "trends-stat-arrow", arrowFor(c.pct), value);
      htmlEl("span", null, formatChange(c.pct), value);
    });

    var W = Math.max(canvas.clientWidth, 320), H = 280;
    var m = { l: 48, r: 12, t: 22, b: 28 };
    var innerW = W - m.l - m.r, innerH = H - m.t - m.b;
    var yMax = niceMax(Math.max.apply(null, counts));
    function y(v) { return m.t + innerH * (1 - v / yMax); }

    var svg = svgEl("svg", { width: W, height: H, viewBox: "0 0 " + W + " " + H, class: "trends-svg", role: "group",
      "aria-label": "Reports from " + windowLabel + " for each year, " + years[0] + " to " + years[cur] }, canvas);
    var step = yMax / 4;
    for (var g = 0; g <= 4; g++) {
      var gy = y(step * g);
      svgEl("line", { x1: m.l, x2: W - m.r, y1: gy, y2: gy, class: "trends-grid" }, svg);
      var label = svgEl("text", { x: m.l - 8, y: gy + 4, class: "trends-axis", "text-anchor": "end" }, svg);
      label.textContent = fmt(step * g);
    }

    var slot = innerW / n;
    var barW = Math.min(28, slot * 0.6);
    var tip = tooltipFor(canvas);

    years.forEach(function (year, i) {
      var cx = m.l + slot * (i + 0.5);
      var top = y(counts[i]);
      var height = Math.max(0, m.t + innerH - top);
      var group = svgEl("g", { class: "trends-bar", tabindex: "0", role: "img",
        "aria-label": year + ": " + fmt(counts[i]) + " reports, " + windowLabel }, svg);
      // Hit area wider than the bar.
      svgEl("rect", { x: cx - slot / 2, y: m.t, width: slot, height: innerH, class: "trends-hit" }, group);
      var radius = Math.min(4, barW / 2, height);
      // Rounded data-end, square at the baseline.
      var d = "M" + (cx - barW / 2) + "," + (m.t + innerH) +
        "V" + (top + radius) + "Q" + (cx - barW / 2) + "," + top + " " + (cx - barW / 2 + radius) + "," + top +
        "H" + (cx + barW / 2 - radius) + "Q" + (cx + barW / 2) + "," + top + " " + (cx + barW / 2) + "," + (top + radius) +
        "V" + (m.t + innerH) + "Z";
      svgEl("path", { d: d, class: "trends-bar-fill" + (i === cur ? " is-current" : "") }, group);
      var val = svgEl("text", { x: cx, y: top - 6, class: "trends-bar-value", "text-anchor": "middle" }, group);
      val.textContent = fmt(counts[i]);
      var yl = svgEl("text", { x: cx, y: H - 8, class: "trends-axis", "text-anchor": "middle" }, svg);
      yl.textContent = String(year);

      function show() {
        tip.textContent = "";
        htmlEl("div", "trends-tip-title", year + (i === cur ? " (so far)" : ""), tip);
        var row = htmlEl("div", "trends-tip-row", null, tip);
        htmlEl("strong", null, fmt(counts[i]), row);
        htmlEl("span", "trends-tip-label", UNIT + ", " + windowLabel, row);
        if (i > 0) {
          var change = percentChange(counts[i], counts[i - 1]);
          var delta = htmlEl("div", "trends-tip-row", null, tip);
          htmlEl("span", "trends-tip-label", formatChange(change) + " from " + years[i - 1], delta);
        }
        placeTooltip(canvas, tip, cx, m.t + 20);
        group.classList.add("is-active");
      }
      function hide() { hideTooltip(canvas); group.classList.remove("is-active"); }
      group.addEventListener("pointerenter", show);
      group.addEventListener("pointerleave", hide);
      group.addEventListener("focus", show);
      group.addEventListener("blur", hide);
    });

    tables.period = {
      headers: ["Year", UNIT.charAt(0).toUpperCase() + UNIT.slice(1) + " " + windowLabel],
      rows: years.map(function (year, i) { return [String(year), counts[i]]; })
    };
  }

  // ---------- chart 3: seasonality heatmap ----------

  var RAMP = [[252, 232, 217], [249, 185, 138], [234, 88, 12], [143, 47, 6]];

  function rampColor(t) {
    var pos = Math.min(0.999, Math.max(0, t)) * (RAMP.length - 1);
    var i = Math.floor(pos), f = pos - i;
    var c = RAMP[i].map(function (v, k) { return Math.round(v + (RAMP[i + 1][k] - v) * f); });
    return "rgb(" + c.join(",") + ")";
  }

  function renderHeat() {
    var canvas = canvasOf("chart-heat");
    canvas.textContent = "";

    var years = data.years, n = years.length;
    var W = Math.max(canvas.clientWidth, 320);
    var labelW = 44, topH = 22, rowH = 28, gap = 2;
    var H = topH + n * rowH + 36;
    var cellW = (W - labelW) / 12;

    var maxValue = 0;
    data.monthly.forEach(function (series) { series.forEach(function (v) { if (v !== null && v > maxValue) maxValue = v; }); });

    var svg = svgEl("svg", { width: W, height: H, viewBox: "0 0 " + W + " " + H, class: "trends-svg", role: "group",
      "aria-label": "Reports by month and year, darker means more" }, canvas);

    MONTHS.forEach(function (name, mi) {
      var t = svgEl("text", { x: labelW + cellW * (mi + 0.5), y: 14, class: "trends-axis", "text-anchor": "middle" }, svg);
      t.textContent = name;
    });

    var tip = tooltipFor(canvas);
    var throughParts = data.through.split("-").map(Number);
    var partialMonth = throughParts[2] < new Date(throughParts[0], throughParts[1], 0).getDate() ? throughParts[1] - 1 : -1;

    years.forEach(function (year, i) {
      var yt = svgEl("text", { x: labelW - 8, y: topH + rowH * i + rowH / 2 + 4, class: "trends-axis", "text-anchor": "end" }, svg);
      yt.textContent = String(year);
      for (var mi = 0; mi < 12; mi++) {
        (function (mi) {
          var v = data.monthly[i][mi];
          var cx = labelW + cellW * mi, cy = topH + rowH * i;
          var isPartial = i === n - 1 && mi === partialMonth;
          var cell = svgEl("rect", {
            x: cx + gap / 2, y: cy + gap / 2, width: cellW - gap, height: rowH - gap, rx: 3,
            class: "trends-cell" + (v === null ? " is-empty" : "") + (isPartial ? " is-partial" : ""),
            tabindex: v === null ? "-1" : "0", role: "img",
            "aria-label": v === null ? "" : MONTHS[mi] + " " + year + ": " + fmt(v) + " " + UNIT + (isPartial ? " so far" : "")
          }, svg);
          if (v === null) return;
          cell.style.fill = rampColor(maxValue ? v / maxValue : 0);
          function show() {
            tip.textContent = "";
            htmlEl("div", "trends-tip-title", MONTHS[mi] + " " + year + (isPartial ? " (through " + monthDayLabel(data.through) + ")" : ""), tip);
            var row = htmlEl("div", "trends-tip-row", null, tip);
            htmlEl("strong", null, fmt(v), row);
            htmlEl("span", "trends-tip-label", UNIT, row);
            placeTooltip(canvas, tip, cx + cellW / 2, cy + rowH / 2);
            cell.classList.add("is-active");
          }
          function hide() { hideTooltip(canvas); cell.classList.remove("is-active"); }
          cell.addEventListener("pointerenter", show);
          cell.addEventListener("pointerleave", hide);
          cell.addEventListener("focus", show);
          cell.addEventListener("blur", hide);
        })(mi);
      }
    });

    // Scale key: low to high.
    var keyY = topH + n * rowH + 14, keyW = Math.min(200, W - labelW);
    var defs = svgEl("defs", {}, svg);
    var grad = svgEl("linearGradient", { id: "heat-ramp" }, defs);
    for (var s = 0; s <= 4; s++) svgEl("stop", { offset: (s * 25) + "%", "stop-color": rampColor(s / 4) }, grad);
    svgEl("rect", { x: labelW, y: keyY, width: keyW, height: 8, rx: 4, fill: "url(#heat-ramp)" }, svg);
    var lo = svgEl("text", { x: labelW, y: keyY + 22, class: "trends-axis" }, svg);
    lo.textContent = "0";
    var hi = svgEl("text", { x: labelW + keyW, y: keyY + 22, class: "trends-axis", "text-anchor": "end" }, svg);
    hi.textContent = fmt(maxValue) + " in a month";

    tables.heat = {
      headers: ["Year"].concat(MONTHS),
      rows: years.map(function (year, i) {
        return [String(year)].concat(data.monthly[i].map(function (v) { return v === null ? "" : v; }));
      })
    };
  }

  // ---------- chart 4: where ----------

  function sparkline(counts, color) {
    var W = 84, H = 22, pad = 3;
    var max = Math.max.apply(null, counts) || 1;
    var svg = svgEl("svg", { width: W, height: H, viewBox: "0 0 " + W + " " + H, class: "trends-spark", "aria-hidden": "true" });
    var pts = counts.map(function (v, i) {
      return (pad + (W - pad * 2) * i / (counts.length - 1)) + "," + (H - pad - (H - pad * 2) * v / max);
    });
    svgEl("polyline", { points: pts.join(" "), class: "trends-spark-line", style: "stroke:" + color }, svg);
    var last = pts[pts.length - 1].split(",");
    svgEl("circle", { cx: last[0], cy: last[1], r: 2.5, class: "trends-spark-dot", style: "fill:" + color }, svg);
    return svg;
  }

  function renderAreas() {
    var canvas = canvasOf("chart-areas");
    canvas.textContent = "";
    var list = state.areasTab === "wards" ? data.wards : data.neighborhoods;
    var years = data.years, cur = years.length - 1;

    if (!list.length) {
      htmlEl("p", "trends-empty", "No " + UNIT + " in this period.", canvas);
    } else {
      var byRate = state.sort === "rate";
      var metric = function (a) { return byRate ? (a.ratePerThousand || 0) : a.counts[cur]; };
      var max = Math.max.apply(null, list.map(metric)) || 1;
      var ol = htmlEl("ol", "trends-areas", null, canvas);
      list.forEach(function (area, i) {
        var li = htmlEl("li", "trends-area", null, ol);
        htmlEl("span", "trends-area-rank", String(i + 1), li);
        var nameCell = htmlEl("span", "trends-area-namecell", null, li);
        var name = htmlEl("button", "trends-area-name", area.name, nameCell);
        name.type = "button";
        name.title = "Filter the page to " + area.name;
        if (area.url) {
          var page = htmlEl("a", "trends-area-link", "page \u2192", nameCell);
          page.href = area.url;
          page.title = "Open the " + area.name + " page";
        }
        name.addEventListener("click", function () {
          var value = state.areasTab === "wards" ? "ward:" + area.name.replace("Ward ", "") : "n:" + area.name;
          setArea(value);
        });
        var barWrap = htmlEl("span", "trends-area-bar", null, li);
        var fill = htmlEl("span", "trends-area-bar-fill", null, barWrap);
        fill.style.width = (metric(area) / max * 100) + "%";
        var countEl = htmlEl("strong", "trends-area-count", fmt(area.counts[cur]), li);
        if (area.ratePerThousand !== undefined && area.ratePerThousand !== null) {
          htmlEl("small", "trends-area-rate", area.ratePerThousand.toLocaleString("en-US") + " per 1,000", countEl);
        }
        var change = htmlEl("span", "trends-area-change", null, li);
        change.textContent = area.changeVsPrior === null ? "n/a" : arrowFor(area.changeVsPrior) + " " + Math.abs(Math.round(area.changeVsPrior)) + "%";
        change.title = "vs " + years[cur - 1] + ", same period";
        var spark = htmlEl("span", "trends-area-spark", null, li);
        spark.appendChild(sparkline(area.counts, "var(--accent)"));
        spark.title = years[0] + " to " + years[cur] + ", same period each year";
      });
    }

    tables.areas = {
      headers: ["Area"].concat(years.map(function (y) { return y + " (Jan 1 to " + monthDayLabel(data.through) + ")"; })),
      rows: list.map(function (a) { return [a.name].concat(a.counts); })
    };
    if (list.length && list[0].ratePerThousand !== undefined) {
      tables.areas.headers.push("Per 1,000 residents, " + years[cur], "Residents (2020)");
      tables.areas.rows = list.map(function (a, i) {
        return tables.areas.rows[i].concat([a.ratePerThousand === null ? "" : a.ratePerThousand, a.population === null ? "" : a.population]);
      });
    }
    var sortBox = document.getElementById("areas-sort");
    if (sortBox) {
      sortBox.hidden = !(list.length && list[0].ratePerThousand !== undefined);
      sortBox.querySelectorAll("[data-sort]").forEach(function (b) {
        var active = b.dataset.sort === state.sort;
        b.classList.toggle("is-active", active);
        b.setAttribute("aria-pressed", active ? "true" : "false");
      });
    }
  }

  // ---------- offense mix: ranked bars with change and a line per year ----------

  function renderMix() {
    var box = document.getElementById("chart-mix");
    if (!box || !data.groups) return;
    var canvas = canvasOf("chart-mix");
    canvas.textContent = "";
    var years = data.years, cur = years.length - 1;
    var list = data.groups.filter(function (g) { return g.isCrime && g.counts[cur] > 0; })
      .sort(function (a, b) { return b.counts[cur] - a.counts[cur]; });
    if (!list.length) {
      htmlEl("p", "trends-empty", "No " + UNIT + " in this period.", canvas);
      return;
    }
    var max = list[0].counts[cur] || 1;
    var ol = htmlEl("ol", "trends-areas", null, canvas);
    list.forEach(function (g, i) {
      var li = htmlEl("li", "trends-area", null, ol);
      htmlEl("span", "trends-area-rank", String(i + 1), li);
      var nameCell = htmlEl("span", "trends-area-namecell", null, li);
      var name = htmlEl("button", "trends-area-name", g.label, nameCell);
      name.type = "button";
      name.title = g.definition;
      name.addEventListener("click", function () {
        categorySelect.value = g.key;
        state.category = g.key;
        load();
        section("trends").scrollIntoView({ behavior: "smooth", block: "start" });
      });
      var barWrap = htmlEl("span", "trends-area-bar", null, li);
      var fill = htmlEl("span", "trends-area-bar-fill", null, barWrap);
      fill.style.width = (g.counts[cur] / max * 100) + "%";
      htmlEl("strong", "trends-area-count", fmt(g.counts[cur]), li);
      var change = htmlEl("span", "trends-area-change", null, li);
      change.textContent = g.changeVsPrior === null ? "n/a" : arrowFor(g.changeVsPrior) + " " + Math.abs(Math.round(g.changeVsPrior)) + "%";
      change.title = "vs " + years[cur - 1] + ", same period";
      var spark = htmlEl("span", "trends-area-spark", null, li);
      spark.appendChild(sparkline(g.counts, "var(--accent)"));
      spark.title = years[0] + " to " + years[cur] + ", same period each year";
    });
  }

  // ---------- ward map: a choropleth of the 13 council wards ----------

  var wardGeo = null;
  var wardGeoRequested = false;
  var mapMetric = "";

  function ringsOf(geometry) {
    return geometry.type === "Polygon" ? [geometry.coordinates] : geometry.coordinates;
  }

  function renderMap() {
    var box = document.getElementById("chart-map");
    if (!box || !data.wards) return;
    var canvas = canvasOf("chart-map");

    if (!wardGeo) {
      if (!wardGeoRequested) {
        wardGeoRequested = true;
        fetch("/api/wards")
          .then(function (r) { if (!r.ok) throw new Error("HTTP " + r.status); return r.json(); })
          .then(function (json) { wardGeo = json.features; if (data) renderMap(); })
          .catch(function () { canvas.textContent = ""; htmlEl("p", "trends-empty", "The ward map couldn't load.", canvas); });
      }
      return;
    }

    canvas.textContent = "";
    var years = data.years, cur = years.length - 1;
    var byWard = {};
    data.wards.forEach(function (w) { byWard[Number(w.name.replace("Ward ", ""))] = w; });
    var hasRate = data.wards.some(function (w) { return w.ratePerThousand !== undefined && w.ratePerThousand !== null; });
    if (!hasRate) mapMetric = "count";
    else if (!mapMetric) mapMetric = "rate";

    box.querySelectorAll("[data-map-metric]").forEach(function (b) {
      var active = b.dataset.mapMetric === mapMetric;
      b.hidden = !hasRate;
      b.classList.toggle("is-active", active);
      b.setAttribute("aria-pressed", active ? "true" : "false");
    });

    function valueOf(w) {
      if (!w) return 0;
      return mapMetric === "rate" ? (w.ratePerThousand || 0) : w.counts[cur];
    }
    function label(v) { return mapMetric === "rate" ? v.toLocaleString("en-US", { maximumFractionDigits: 1 }) : fmt(v); }

    var values = wardGeo.map(function (f) { return valueOf(byWard[f.properties.ward]); });
    var lo = Math.min.apply(null, values), hi = Math.max.apply(null, values);
    var span = hi - lo || 1;

    // Equirectangular, scaled by cos(latitude) so the wards keep their shape.
    var minLng = Infinity, maxLng = -Infinity, minLat = Infinity, maxLat = -Infinity;
    wardGeo.forEach(function (f) {
      ringsOf(f.geometry).forEach(function (poly) {
        poly[0].forEach(function (pt) {
          minLng = Math.min(minLng, pt[0]); maxLng = Math.max(maxLng, pt[0]);
          minLat = Math.min(minLat, pt[1]); maxLat = Math.max(maxLat, pt[1]);
        });
      });
    });
    var k = Math.cos((minLat + maxLat) / 2 * Math.PI / 180);
    var worldW = (maxLng - minLng) * k, worldH = maxLat - minLat;
    var W = Math.min(Math.max(canvas.clientWidth, 300), 560), pad = 6;
    var scale = (W - pad * 2) / worldW;
    var H = Math.round(worldH * scale + pad * 2);
    function px(lng) { return pad + (lng - minLng) * k * scale; }
    function py(lat) { return pad + (maxLat - lat) * scale; }

    var svg = svgEl("svg", { width: W, height: H, viewBox: "0 0 " + W + " " + H, class: "trends-svg ward-map", role: "group",
      "aria-label": "Map of Minneapolis council wards shaded by " + (mapMetric === "rate" ? "offenses per 1,000 residents" : "offenses") + ", Jan 1 to " + monthDayLabel(data.through) }, canvas);
    var tip = tooltipFor(canvas);

    wardGeo.forEach(function (f) {
      var n = f.properties.ward, w = byWard[n], v = valueOf(w);
      var t = (v - lo) / span;
      var g = svgEl("g", { class: "ward-shape", tabindex: "0", role: "img",
        "aria-label": "Ward " + n + ": " + label(v) + (mapMetric === "rate" ? " per 1,000 residents" : " offenses") }, svg);
      var d = ringsOf(f.geometry).map(function (poly) {
        return poly.map(function (ring) {
          return "M" + ring.map(function (pt) { return px(pt[0]).toFixed(1) + "," + py(pt[1]).toFixed(1); }).join("L") + "Z";
        }).join("");
      }).join("");
      svgEl("path", { d: d, fill: rampColor(t), class: "ward-path", "fill-rule": "evenodd" }, g);
      var text = svgEl("text", { x: px(f.properties.labelLng), y: py(f.properties.labelLat) + 4, "text-anchor": "middle",
        class: "ward-label" + (t > 0.55 ? " is-light" : "") }, g);
      text.textContent = String(n);

      function show() {
        tip.textContent = "";
        htmlEl("div", "trends-tip-title", "Ward " + n + (f.properties.name ? " (" + f.properties.name + ")" : ""), tip);
        if (w) {
          var row = htmlEl("div", "trends-tip-row", null, tip);
          htmlEl("strong", null, fmt(w.counts[cur]), row);
          htmlEl("span", "trends-tip-label", UNIT + " so far in " + years[cur], row);
          if (w.ratePerThousand !== undefined && w.ratePerThousand !== null) {
            var r2 = htmlEl("div", "trends-tip-row", null, tip);
            htmlEl("strong", null, w.ratePerThousand.toLocaleString("en-US"), r2);
            htmlEl("span", "trends-tip-label", "per 1,000 residents", r2);
          }
          if (w.changeVsPrior !== null && w.changeVsPrior !== undefined) {
            htmlEl("div", "trends-tip-label", formatChange(w.changeVsPrior) + " from " + years[cur - 1], tip);
          }
        }
        var box2 = g.getBoundingClientRect(), c = canvas.getBoundingClientRect();
        placeTooltip(canvas, tip, box2.left - c.left + box2.width / 2, box2.top - c.top + box2.height / 2);
        g.classList.add("is-active");
      }
      function hide() { hideTooltip(canvas); g.classList.remove("is-active"); }
      g.addEventListener("pointerenter", show);
      g.addEventListener("pointerleave", hide);
      g.addEventListener("focus", show);
      g.addEventListener("blur", hide);
      g.addEventListener("click", function () { setArea("ward:" + n); });
    });

    var legend = htmlEl("div", "ward-legend", null, canvas);
    htmlEl("span", null, label(lo), legend);
    var ramp = htmlEl("span", "ward-legend-ramp", null, legend);
    ramp.style.background = "linear-gradient(to right," + [0, 0.33, 0.66, 1].map(function (t) { return rampColor(t); }).join(",") + ")";
    htmlEl("span", null, label(hi) + (mapMetric === "rate" ? " per 1,000 residents" : " offenses"), legend);
  }

  // ---------- hour by weekday ----------

  function renderHour() {
    var section = document.getElementById("chart-hour");
    if (!section || !data.hourWeekday) return;
    var canvas = canvasOf("chart-hour");
    canvas.textContent = "";

    var days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    var hourLabel = function (h) { return (h % 12 === 0 ? 12 : h % 12) + (h < 12 ? " AM" : " PM"); };
    var W = Math.max(canvas.clientWidth, 320);
    var labelW = 38, topH = 20, rowH = W < 520 ? 22 : 26, gap = 2;
    var cellW = (W - labelW) / 24;
    var H = topH + 7 * rowH + 38;

    var maxValue = 0;
    data.hourWeekday.forEach(function (row) { row.forEach(function (v) { if (v > maxValue) maxValue = v; }); });

    var svg = svgEl("svg", { width: W, height: H, viewBox: "0 0 " + W + " " + H, class: "trends-svg", role: "group",
      "aria-label": "Offenses by hour of day and weekday over the last 12 months, darker means more" }, canvas);

    [0, 3, 6, 9, 12, 15, 18, 21].forEach(function (h) {
      var t = svgEl("text", { x: labelW + cellW * h + cellW / 2, y: 13, class: "trends-axis", "text-anchor": "middle" }, svg);
      t.textContent = W < 520 ? String(h % 12 === 0 ? 12 : h % 12) + (h < 12 ? "a" : "p") : hourLabel(h);
    });

    var tip = tooltipFor(canvas);
    days.forEach(function (day, di) {
      var dt = svgEl("text", { x: labelW - 8, y: topH + rowH * di + rowH / 2 + 4, class: "trends-axis", "text-anchor": "end" }, svg);
      dt.textContent = day;
      for (var h = 0; h < 24; h++) {
        (function (h) {
          var v = data.hourWeekday[di][h];
          var cx = labelW + cellW * h, cy = topH + rowH * di;
          var cell = svgEl("rect", { x: cx + gap / 2, y: cy + gap / 2, width: cellW - gap, height: rowH - gap, rx: 2, class: "trends-cell", tabindex: "0", role: "img",
            "aria-label": day + " " + hourLabel(h) + ": " + fmt(v) + " " + UNIT }, svg);
          cell.style.fill = rampColor(maxValue ? v / maxValue : 0);
          function show() {
            tip.textContent = "";
            htmlEl("div", "trends-tip-title", day + ", " + hourLabel(h) + " to " + hourLabel((h + 1) % 24), tip);
            var row = htmlEl("div", "trends-tip-row", null, tip);
            htmlEl("strong", null, fmt(v), row);
            htmlEl("span", "trends-tip-label", UNIT + ", last 12 months", row);
            placeTooltip(canvas, tip, cx + cellW / 2, cy + rowH / 2);
            cell.classList.add("is-active");
          }
          function hide() { hideTooltip(canvas); cell.classList.remove("is-active"); }
          cell.addEventListener("pointerenter", show);
          cell.addEventListener("pointerleave", hide);
          cell.addEventListener("focus", show);
          cell.addEventListener("blur", hide);
        })(h);
      }
    });

    var keyY = topH + 7 * rowH + 12, keyW = Math.min(200, W - labelW);
    var defs = svgEl("defs", {}, svg);
    var grad = svgEl("linearGradient", { id: "hour-ramp" }, defs);
    for (var s = 0; s <= 4; s++) svgEl("stop", { offset: (s * 25) + "%", "stop-color": rampColor(s / 4) }, grad);
    svgEl("rect", { x: labelW, y: keyY, width: keyW, height: 8, rx: 4, fill: "url(#hour-ramp)" }, svg);
    var lo = svgEl("text", { x: labelW, y: keyY + 22, class: "trends-axis" }, svg);
    lo.textContent = "0";
    var hi = svgEl("text", { x: labelW + keyW, y: keyY + 22, class: "trends-axis", "text-anchor": "end" }, svg);
    hi.textContent = fmt(maxValue) + " in an hour slot";

    tables.hour = {
      headers: ["Weekday"].concat(Array.apply(null, Array(24)).map(function (_, h) { return hourLabel(h); })),
      rows: days.map(function (day, di) { return [day].concat(data.hourWeekday[di]); })
    };
  }

  // ---------- offense groups ----------

  function renderGroups() {
    var box = document.getElementById("groups-body");
    if (!box || !data.groups) return;
    box.textContent = "";
    var years = data.years, cur = years.length - 1;
    var windowLabel = "Jan 1 to " + monthDayLabel(data.through);

    var table = htmlEl("table", "area-table groups-table", null, box);
    htmlEl("caption", "groups-caption", windowLabel + ", " + data.scope + ". Offenses, counted per MPD's offense count.", table);
    var thead = htmlEl("thead", null, null, table);
    var hr = htmlEl("tr", null, null, thead);
    var cols = ["Group", String(years[cur]), String(years[cur - 1]), "Change from " + years[cur - 1], "Change from " + years[0]];
    if (data.population) cols.push("Per 1,000 residents");
    cols.forEach(function (h) { htmlEl("th", null, h, hr); });
    var tbody = htmlEl("tbody", null, null, table);

    function addRow(g) {
      var tr = htmlEl("tr", null, null, tbody);
      var first = htmlEl("td", null, null, tr);
      var btn = htmlEl("button", "trends-area-name", g.label, first);
      btn.type = "button";
      btn.title = g.definition;
      btn.addEventListener("click", function () {
        categorySelect.value = g.key;
        state.category = g.key;
        load();
        section("trends").scrollIntoView({ behavior: "smooth", block: "start" });
      });
      htmlEl("td", null, fmt(g.counts[cur]), tr);
      htmlEl("td", null, fmt(g.counts[cur - 1]), tr);
      htmlEl("td", null, g.changeVsPrior === null ? "" : formatChange(g.changeVsPrior), tr);
      htmlEl("td", null, g.changeVsBase === null ? "" : formatChange(g.changeVsBase), tr);
      if (data.population) htmlEl("td", null, g.ratePerThousand === null ? "" : g.ratePerThousand.toLocaleString("en-US"), tr);
    }

    data.groups.forEach(addRow);

    var total = data.groups.reduce(function (acc, g) { return acc.map(function (v, i) { return v + g.counts[i]; }); }, years.map(function () { return 0; }));
    var totalRow = htmlEl("tr", "groups-total", null, tbody);
    htmlEl("td", null, "All crimes", totalRow);
    htmlEl("td", null, fmt(total[cur]), totalRow);
    htmlEl("td", null, fmt(total[cur - 1]), totalRow);
    htmlEl("td", null, formatChange(percentChange(total[cur], total[cur - 1])), totalRow);
    htmlEl("td", null, formatChange(percentChange(total[cur], total[0])), totalRow);
    if (data.population) htmlEl("td", null, (Math.round(total[cur] * 10000 / data.population) / 10).toLocaleString("en-US"), totalRow);

    var sep = htmlEl("tr", "near-sep", null, tbody);
    var sepCell = htmlEl("td", null, "Not counted as crimes: calls and subsets of the offenses above", sep);
    sepCell.colSpan = cols.length;
    data.metrics.forEach(addRow);

    tables.groups = {
      headers: ["Group", "Counted as crime"].concat(years.map(function (y) { return y + " (" + windowLabel + ")"; })),
      rows: data.groups.concat(data.metrics).map(function (g) { return [g.label, g.isCrime ? "yes" : "no"].concat(g.counts); })
    };
  }

  // ---------- tables and CSV ----------

  function renderTables() {
    Object.keys(tables).forEach(function (key) {
      var id = { yoy: "chart-yoy", period: "chart-period", heat: "chart-heat", areas: "chart-areas", hour: "chart-hour", groups: "chart-groups" }[key];
      var host = id && section(id);
      var details = host && host.querySelector("[data-table]");
      if (!details) return;
      details.querySelectorAll("table").forEach(function (old) { old.remove(); });
      var table = htmlEl("table", null, null, details);
      var thead = htmlEl("thead", null, null, table);
      var hr = htmlEl("tr", null, null, thead);
      tables[key].headers.forEach(function (h) { htmlEl("th", null, h, hr); });
      var tbody = htmlEl("tbody", null, null, table);
      tables[key].rows.forEach(function (row) {
        var tr = htmlEl("tr", null, null, tbody);
        row.forEach(function (cell) { htmlEl("td", null, cell === "" ? "" : (typeof cell === "number" ? fmt(cell) : String(cell)), tr); });
      });
    });
  }

  function csvCell(value) {
    var text = String(value);
    return /[",\n]/.test(text) ? '"' + text.replace(/"/g, '""') + '"' : text;
  }

  function downloadCsv(key) {
    var t = tables[key];
    if (!t) return;
    var lines = [t.headers].concat(t.rows).map(function (row) { return row.map(csvCell).join(","); });
    var blob = new Blob([lines.join("\n") + "\n"], { type: "text/csv;charset=utf-8" });
    var link = document.createElement("a");
    link.href = URL.createObjectURL(blob);
    link.download = "glass-on-the-street-" + key + ".csv";
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(link.href);
  }

  // ---------- filters and loading ----------

  var categorySelect = document.getElementById("filter-category");
  var areaSelect = document.getElementById("filter-area");
  var resetButton = document.getElementById("filter-reset");

  // The filters that change the data (sent to the API).
  function queryString() {
    var params = new URLSearchParams();
    if (state.category) params.set("category", state.category);
    if (state.area.indexOf("ward:") === 0) params.set("ward", state.area.slice(5));
    if (state.area.indexOf("n:") === 0) params.set("neighborhood", state.area.slice(2));
    if (state.sort) params.set("sort", state.sort);
    return params.toString();
  }

  // The same filters plus which years are switched off, so a shared link restores both.
  function syncUrl() {
    var params = new URLSearchParams(queryString());
    var hidden = Object.keys(state.hidden);
    if (hidden.length) params.set("hide", hidden.sort().join(","));
    var qs = params.toString();
    history.replaceState(null, "", location.pathname + (qs ? "?" + qs : ""));
  }

  function setArea(value) {
    state.area = value;
    areaSelect.value = value;
    load();
    section("trends").scrollIntoView({ behavior: "smooth", block: "start" });
  }

  function fillAreaOptions() {
    if (areaSelect.dataset.filled) return;
    areaSelect.dataset.filled = "1";
    var wards = document.createElement("optgroup");
    wards.label = "Wards";
    data.options.wards.forEach(function (w) {
      var o = document.createElement("option");
      o.value = "ward:" + w;
      o.textContent = "Ward " + w;
      wards.appendChild(o);
    });
    var hoods = document.createElement("optgroup");
    hoods.label = "Neighborhoods";
    data.options.neighborhoods.forEach(function (name) {
      var o = document.createElement("option");
      o.value = "n:" + name;
      o.textContent = name;
      hoods.appendChild(o);
    });
    areaSelect.appendChild(wards);
    areaSelect.appendChild(hoods);
    areaSelect.value = state.area;
  }

  function updateHeadline() {
    var years = data.years, cur = years.length - 1;
    var counts = data.samePeriod;
    var el = document.getElementById("trends-headline");
    var scope = [];
    var catOption = categorySelect.options[categorySelect.selectedIndex];
    scope.push(state.category ? catOption.textContent.toLowerCase() + " " + UNIT : ALL_PHRASE);
    if (state.area) scope.push("in " + areaSelect.options[areaSelect.selectedIndex].textContent);
    el.textContent = "";
    el.appendChild(document.createTextNode("Through " + monthDayLabel(data.through) + ", " + years[cur] + ", MPD has recorded "));
    htmlEl("strong", null, fmt(counts[cur]), el);
    el.appendChild(document.createTextNode(" " + scope.join(" ") + " since Jan 1"));
    var vsPrior = percentChange(counts[cur], counts[cur - 1]);
    var vsBase = percentChange(counts[cur], counts[0]);
    var tail = "";
    if (vsPrior !== null) tail += ": " + formatChange(vsPrior) + " from " + years[cur - 1];
    if (vsBase !== null) tail += (tail ? ", and " : ": ") + formatChange(vsBase) + " from " + years[0];
    el.appendChild(document.createTextNode(tail + "."));
  }

  function renderAll() {
    renderYoY();
    renderPeriod();
    renderHeat();
    renderAreas();
    renderHour();
    renderMix();
    renderMap();
    renderGroups();
    renderTables();
    updateHeadline();
    resetButton.hidden = !state.category && !state.area;
  }

  function load() {
    var id = ++requestId;
    var qs = queryString();
    root.classList.add("is-loading");
    syncUrl();
    fetch(API + (qs ? "?" + qs : ""))
      .then(function (r) { if (!r.ok) throw new Error("HTTP " + r.status); return r.json(); })
      .then(function (json) {
        if (id !== requestId) return;
        data = json;
        fillAreaOptions();
        renderAll();
      })
      .catch(function () {
        if (id !== requestId) return;
        document.getElementById("trends-headline").textContent = "The charts couldn't load. Try refreshing the page.";
      })
      .then(function () { if (id === requestId) root.classList.remove("is-loading"); });
  }

  categorySelect.addEventListener("change", function () { state.category = categorySelect.value; load(); });
  areaSelect.addEventListener("change", function () { state.area = areaSelect.value; load(); });
  resetButton.addEventListener("click", function () {
    state.category = "";
    state.area = "";
    categorySelect.value = "";
    areaSelect.value = "";
    load();
  });

  root.querySelectorAll("[data-download]").forEach(function (button) {
    button.addEventListener("click", function () { downloadCsv(button.dataset.download); });
  });

  root.querySelectorAll("[data-areas-tab]").forEach(function (tab) {
    tab.addEventListener("click", function () {
      state.areasTab = tab.dataset.areasTab;
      root.querySelectorAll("[data-areas-tab]").forEach(function (t) {
        var active = t === tab;
        t.classList.toggle("is-active", active);
        t.setAttribute("aria-selected", active ? "true" : "false");
      });
      if (data) { renderAreas(); renderTables(); }
    });
  });

  root.querySelectorAll("[data-map-metric]").forEach(function (button) {
    button.addEventListener("click", function () {
      mapMetric = button.dataset.mapMetric;
      if (data) renderMap();
    });
  });

  root.querySelectorAll("[data-sort]").forEach(function (button) {
    button.addEventListener("click", function () {
      state.sort = button.dataset.sort;
      load();
    });
  });

  var resizeTimer = null;
  window.addEventListener("resize", function () {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(function () { if (data) renderAll(); }, 150);
  });

  // Restore filters from the URL so a filtered view can be shared.
  var params = new URLSearchParams(location.search);
  state.category = params.get("category") || "";
  state.sort = params.get("sort") === "rate" ? "rate" : "";
  if (params.get("ward")) state.area = "ward:" + params.get("ward");
  if (params.get("neighborhood")) state.area = "n:" + params.get("neighborhood");
  categorySelect.value = state.category;
  (params.get("hide") || "").split(",").forEach(function (y) { if (/^\d{4}$/.test(y)) state.hidden[y] = true; });

  load();
})();
