// /trends dashboard. Fetches /api/trends for the current filters and draws
// four views from the one response: year-over-year lines, same-period
// columns, a month-by-year heatmap and the area rankings. Charts are plain
// SVG/HTML; every label that comes from the API goes in with textContent.
(function () {
  var root = document.getElementById("trends");
  if (!root) return;

  var SVGNS = "http://www.w3.org/2000/svg";
  var MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
  var fmt = function (n) { return formatNumber(n); };

  var state = { category: "", area: "", areasTab: "neighborhoods", hidden: {} };
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
    var steps = [1, 2, 2.5, 5, 10];
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
      htmlEl("span", null, MONTHS[partialMonth] + " " + years[cur] + " so far (month not over)", partialItem);
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
      "aria-label": "Monthly reports for " + years[0] + " to " + years[cur] + ". Use the left and right arrow keys to move between months." }, canvas);

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
        htmlEl("span", "trends-tip-label", "reports, " + windowLabel, row);
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
      headers: ["Year", "Reports " + windowLabel],
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
            "aria-label": v === null ? "" : MONTHS[mi] + " " + year + ": " + fmt(v) + " reports" + (isPartial ? " so far" : "")
          }, svg);
          if (v === null) return;
          cell.style.fill = rampColor(maxValue ? v / maxValue : 0);
          function show() {
            tip.textContent = "";
            htmlEl("div", "trends-tip-title", MONTHS[mi] + " " + year + (isPartial ? " (through " + monthDayLabel(data.through) + ")" : ""), tip);
            var row = htmlEl("div", "trends-tip-row", null, tip);
            htmlEl("strong", null, fmt(v), row);
            htmlEl("span", "trends-tip-label", "reports", row);
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
      htmlEl("p", "trends-empty", "No reports in this period.", canvas);
    } else {
      var max = Math.max.apply(null, list.map(function (a) { return a.counts[cur]; })) || 1;
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
        fill.style.width = (area.counts[cur] / max * 100) + "%";
        htmlEl("strong", "trends-area-count", fmt(area.counts[cur]), li);
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
  }

  // ---------- tables and CSV ----------

  function renderTables() {
    Object.keys(tables).forEach(function (key) {
      var id = { yoy: "chart-yoy", period: "chart-period", heat: "chart-heat", areas: "chart-areas" }[key];
      var details = section(id).querySelector("[data-table]");
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
    scope.push(state.category ? catOption.textContent.toLowerCase() + " reports" : "reports in these categories");
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
    renderTables();
    updateHeadline();
    resetButton.hidden = !state.category && !state.area;
  }

  function load() {
    var id = ++requestId;
    var qs = queryString();
    root.classList.add("is-loading");
    syncUrl();
    fetch("/api/trends" + (qs ? "?" + qs : ""))
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

  var resizeTimer = null;
  window.addEventListener("resize", function () {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(function () { if (data) renderAll(); }, 150);
  });

  // Restore filters from the URL so a filtered view can be shared.
  var params = new URLSearchParams(location.search);
  state.category = params.get("category") || "";
  if (params.get("ward")) state.area = "ward:" + params.get("ward");
  if (params.get("neighborhood")) state.area = "n:" + params.get("neighborhood");
  categorySelect.value = state.category;
  (params.get("hide") || "").split(",").forEach(function (y) { if (/^\d{4}$/.test(y)) state.hidden[y] = true; });

  load();
})();
