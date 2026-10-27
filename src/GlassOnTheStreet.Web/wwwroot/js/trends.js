// Dashboard script for /trends (car-related crime) and /crime (every offense).
// Fetches the page's API (data-api) for the current filters and draws the
// views from the one response: year-over-year lines, same-period columns, a
// month-by-year heatmap, the ward map, the area rankings and, when the response
// carries them, an hour-by-weekday heatmap and an offense-group table. The line,
// column, heatmap and map charts are Apache ECharts (loaded from a CDN before this
// script); the ranked lists and sparklines are plain HTML/SVG. Every label that
// comes from the API goes in with textContent or through esc().
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

  // ---------- ECharts ----------
  // The line, column and heatmap charts and the ward map are Apache ECharts, loaded
  // from a CDN before this script. What gets drawn (colors, partial months, the data
  // window) is decided here; ECharts does the drawing, axes and tooltips. Tooltip
  // text goes through esc(), so nothing from the API reaches innerHTML unescaped.

  var INK = "#18181b", INK_SOFT = "#71717a", RULE = "#e4e4e1", PAPER = "#fafaf9";
  var FONT = "Manrope, -apple-system, BlinkMacSystemFont, sans-serif";
  var axisText = { fontFamily: FONT, fontSize: 11, color: INK_SOFT };

  function esc(text) { return echarts.format.encodeHTML(String(text)); }

  // A fresh chart in `canvas`, or null (with a note in its place) when ECharts didn't load.
  function chartIn(canvas, height, label) {
    if (!window.echarts) {
      canvas.textContent = "";
      htmlEl("p", "trends-empty", "The chart couldn't load.", canvas);
      return null;
    }
    var oldHost = canvas.querySelector("[data-echart]");
    var old = oldHost && echarts.getInstanceByDom(oldHost);
    if (old) old.dispose();
    canvas.textContent = "";
    // The chart gets its own sized element so a scale key can sit under it inside `canvas`.
    // chart-share.js finds the instance through [data-echart].
    var host = htmlEl("div", null, null, canvas);
    host.setAttribute("data-echart", "");
    host.style.height = height + "px";
    host.setAttribute("role", "img");
    host.setAttribute("aria-label", label);
    return echarts.init(host, null, { renderer: "canvas" });
  }

  function tooltipBox(extra) {
    return Object.assign({
      backgroundColor: INK, borderWidth: 0, padding: [8, 11], confine: true,
      textStyle: { color: PAPER, fontFamily: FONT, fontSize: 13 },
      extraCssText: "border-radius:6px;box-shadow:none"
    }, extra);
  }

  function tipTitle(text) { return '<div style="font-size:12px;opacity:.75;margin-bottom:3px">' + esc(text) + "</div>"; }

  function tipRow(value, label, color) {
    return '<div style="display:flex;align-items:baseline;gap:8px">' +
      (color ? '<span style="align-self:center;width:12px;height:2px;flex:none;background:' + color + '"></span>' : "") +
      '<strong style="font-size:15px;min-width:3.2em;font-variant-numeric:tabular-nums">' + esc(value) + "</strong>" +
      '<span style="opacity:.75">' + esc(label) + "</span></div>";
  }

  function tipNote(text) { return '<div style="opacity:.75">' + esc(text) + "</div>"; }

  // ---------- chart 1: year over year ----------

  function renderYoY() {
    var canvas = canvasOf("chart-yoy");
    var legend = section("chart-yoy").querySelector("[data-legend]");
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
      htmlEl("span", null, String(year), item);
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
      htmlEl("span", null, MONTHS[partialMonth] + " " + years[cur] + " through " + monthDayLabel(data.through) + " (the newest days can be incomplete)", partialItem);
    }

    tables.yoy = {
      headers: ["Month"].concat(years.map(String)),
      rows: MONTHS.map(function (name, mi) {
        return [name].concat(years.map(function (_, i) { return data.monthly[i][mi] === null ? "" : data.monthly[i][mi]; }));
      })
    };

    var chart = chartIn(canvas, canvas.clientWidth < 520 ? 280 : 340,
      "Monthly " + UNIT + " for " + years[0] + " to " + years[cur] + ". The table below has the same numbers.");
    if (!chart) return;

    var endLabel = { show: true, fontFamily: FONT, fontSize: 12, fontWeight: 700, color: INK, distance: 8 };
    var series = [];
    // Older years first so the current year paints on top.
    for (var i = 0; i < n; i++) {
      if (isHidden(years[i])) continue;
      var values = data.monthly[i];
      var lastIndex = -1;
      for (var mi = 0; mi < 12; mi++) {
        if (values[mi] === null) break;
        lastIndex = mi;
      }
      var solidEnd = (i === cur && partialMonth >= 0 && lastIndex === partialMonth) ? lastIndex - 1 : lastIndex;
      var color = yearColor(i, n);
      var width = i === cur ? 3 : 2;
      // The current, still-incomplete month is a hollow marker with no line
      // into it: a slope toward a month that isn't over reads as a drop.
      var hollow = i === cur && solidEnd < lastIndex && solidEnd >= 0;
      var line = {
        name: String(years[i]), type: "line", data: values.slice(0, solidEnd + 1),
        symbol: "circle", symbolSize: 8, showSymbol: false,
        lineStyle: { color: color, width: width, join: "round", cap: "round" },
        itemStyle: { color: color, borderColor: PAPER, borderWidth: 2 },
        emphasis: { focus: "none", lineStyle: { width: width } }
      };
      // End labels only for the baseline and the current year; the legend and tooltip carry the rest.
      if ((i === 0 || i === cur) && lastIndex >= 0 && !hollow) line.endLabel = Object.assign({ formatter: String(years[i]) }, endLabel);
      series.push(line);
      if (hollow) {
        series.push({
          type: "scatter", data: [[lastIndex, values[lastIndex]]], symbolSize: 11, z: 10,
          itemStyle: { color: PAPER, borderColor: color, borderWidth: 2.5 },
          label: Object.assign({ position: "right", formatter: String(years[i]) }, endLabel),
          emphasis: { disabled: true }
        });
      }
    }

    chart.setOption({
      animation: false,
      grid: { left: 48, right: 56, top: 12, bottom: 28 },
      xAxis: { type: "category", data: MONTHS, boundaryGap: false, axisLine: { show: false }, axisTick: { show: false },
        axisLabel: Object.assign({ margin: 10 }, axisText) },
      yAxis: { type: "value", min: 0,
        axisLabel: Object.assign({ formatter: function (v) { return fmt(v); } }, axisText),
        splitLine: { lineStyle: { color: RULE } } },
      tooltip: tooltipBox({
        trigger: "axis",
        axisPointer: { type: "line", lineStyle: { color: INK_SOFT, opacity: 0.45 } },
        formatter: function (params) {
          var monthIndex = MONTHS.indexOf(params[0].axisValue);
          if (monthIndex < 0) return "";
          var isPartial = monthIndex === partialMonth;
          var html = tipTitle(MONTHS[monthIndex] + (isPartial && !isHidden(years[cur]) ? " (through " + monthDayLabel(data.through) + " for " + years[cur] + ")" : ""));
          for (var i = n - 1; i >= 0; i--) {
            var v = data.monthly[i][monthIndex];
            if (v === null || isHidden(years[i])) continue;
            html += tipRow(fmt(v), String(years[i]), yearColor(i, n));
          }
          return html;
        }
      }),
      series: series
    });
  }

  // ---------- chart 2: same period ----------

  function renderPeriod() {
    var canvas = canvasOf("chart-period");
    var stats = section("chart-period").querySelector("[data-stats]");
    stats.textContent = "";

    var years = data.years, counts = data.samePeriod;
    var n = years.length, cur = n - 1;
    var windowLabel = "Jan 1 to " + monthDayLabel(data.through);

    var comparisons = [
      { label: "vs " + years[cur - 1], pct: percentChange(counts[cur], counts[cur - 1]) }
    ];
    comparisons.forEach(function (c) {
      var tile = htmlEl("div", "trends-stat", null, stats);
      htmlEl("div", "trends-stat-label", c.label, tile);
      var value = htmlEl("div", "trends-stat-value", null, tile);
      htmlEl("span", "trends-stat-arrow", arrowFor(c.pct), value);
      htmlEl("span", null, formatChange(c.pct), value);
    });

    tables.period = {
      headers: ["Year", UNIT.charAt(0).toUpperCase() + UNIT.slice(1) + " " + windowLabel],
      rows: years.map(function (year, i) { return [String(year), counts[i]]; })
    };

    var chart = chartIn(canvas, 280, UNIT + " from " + windowLabel + " for each year, " + years[0] + " to " + years[cur] + ". The table below has the same numbers.");
    if (!chart) return;

    chart.setOption({
      animation: false,
      grid: { left: 48, right: 12, top: 22, bottom: 28 },
      xAxis: { type: "category", data: years.map(String), axisLine: { show: false }, axisTick: { show: false },
        axisLabel: Object.assign({ margin: 10 }, axisText) },
      yAxis: { type: "value", min: 0,
        axisLabel: Object.assign({ formatter: function (v) { return fmt(v); } }, axisText),
        splitLine: { lineStyle: { color: RULE } } },
      tooltip: tooltipBox({
        trigger: "axis",
        axisPointer: { type: "none" },
        formatter: function (params) {
          var i = params[0].dataIndex;
          var html = tipTitle(years[i] + (i === cur ? " (so far)" : "")) + tipRow(fmt(counts[i]), UNIT + ", " + windowLabel);
          if (i > 0) html += tipNote(formatChange(percentChange(counts[i], counts[i - 1])) + " from " + years[i - 1]);
          return html;
        }
      }),
      series: [{
        type: "bar", barMaxWidth: 28,
        data: counts.map(function (v, i) {
          return {
            value: v,
            itemStyle: { color: i === cur ? CURRENT_COLOR : "#b4b3ad", borderRadius: [4, 4, 0, 0] },
            emphasis: { itemStyle: { color: i === cur ? "#c2410c" : "#77766f" } }
          };
        }),
        label: { show: true, position: "top", distance: 6, fontFamily: FONT, fontSize: 11, fontWeight: 600, color: INK,
          formatter: function (p) { return fmt(p.value); } }
      }]
    });
  }

  // ---------- chart 3: seasonality heatmap ----------

  var RAMP = [[252, 232, 217], [249, 185, 138], [234, 88, 12], [143, 47, 6]];

  function rampColor(t) {
    var pos = Math.min(0.999, Math.max(0, t)) * (RAMP.length - 1);
    var i = Math.floor(pos), f = pos - i;
    var c = RAMP[i].map(function (v, k) { return Math.round(v + (RAMP[i + 1][k] - v) * f); });
    return "rgb(" + c.join(",") + ")";
  }

  // A heatmap on two category axes. Each cell is { value: [x, y, v], itemStyle } and is colored
  // here (rampColor), so the scale key below the chart is plain HTML, not a visualMap.
  function drawHeatmap(canvas, spec) {
    var chart = chartIn(canvas, spec.top + spec.ys.length * spec.rowH + 4, spec.aria);
    if (!chart) return;
    chart.setOption({
      animation: false,
      grid: { left: spec.labelW, right: 0, top: spec.top, bottom: 4 },
      xAxis: { type: "category", data: spec.xs, position: "top", axisLine: { show: false }, axisTick: { show: false },
        axisLabel: Object.assign({ interval: spec.xInterval, margin: 6, formatter: spec.xFormatter }, axisText) },
      yAxis: { type: "category", data: spec.ys, inverse: true, axisLine: { show: false }, axisTick: { show: false },
        axisLabel: Object.assign({ margin: 8 }, axisText) },
      tooltip: tooltipBox({ trigger: "item", formatter: function (p) { return spec.tip(p.data); } }),
      series: [{
        type: "heatmap", data: spec.cells,
        itemStyle: { borderColor: PAPER, borderWidth: 1, borderRadius: spec.radius },
        emphasis: { itemStyle: { borderColor: INK, borderWidth: 1.5, shadowBlur: 0 } }
      }]
    });
  }

  // Low-to-high key under a heatmap or map, in HTML.
  function scaleKey(canvas, lowText, highText) {
    var key = htmlEl("div", "ward-legend", null, canvas);
    htmlEl("span", null, lowText, key);
    var ramp = htmlEl("span", "ward-legend-ramp", null, key);
    ramp.style.background = "linear-gradient(to right," + [0, 0.33, 0.66, 1].map(function (t) { return rampColor(t); }).join(",") + ")";
    htmlEl("span", null, highText, key);
  }

  function renderHeat() {
    var canvas = canvasOf("chart-heat");
    var years = data.years, n = years.length;

    tables.heat = {
      headers: ["Year"].concat(MONTHS),
      rows: years.map(function (year, i) {
        return [String(year)].concat(data.monthly[i].map(function (v) { return v === null ? "" : v; }));
      })
    };

    var maxValue = 0;
    data.monthly.forEach(function (series) { series.forEach(function (v) { if (v !== null && v > maxValue) maxValue = v; }); });

    var throughParts = data.through.split("-").map(Number);
    var partialMonth = throughParts[2] < new Date(throughParts[0], throughParts[1], 0).getDate() ? throughParts[1] - 1 : -1;

    var cells = [];
    years.forEach(function (year, i) {
      for (var mi = 0; mi < 12; mi++) {
        var v = data.monthly[i][mi];
        if (v === null) continue;
        var isPartial = i === n - 1 && mi === partialMonth;
        var style = { color: rampColor(maxValue ? v / maxValue : 0) };
        if (isPartial) { style.borderColor = INK; style.borderWidth = 1.5; style.borderType = "dashed"; }
        cells.push({ value: [mi, i, v], year: year, partial: isPartial, itemStyle: style });
      }
    });

    drawHeatmap(canvas, {
      xs: MONTHS, ys: years.map(String), cells: cells, labelW: 44, top: 22, rowH: 28, radius: 3, xInterval: 0,
      aria: "Reports by month and year, darker means more. The table below has the same numbers.",
      tip: function (cell) {
        var mi = cell.value[0];
        return tipTitle(MONTHS[mi] + " " + cell.year + (cell.partial ? " (through " + monthDayLabel(data.through) + ")" : "")) + tipRow(fmt(cell.value[2]), UNIT);
      }
    });
    if (window.echarts) scaleKey(canvas, "0", fmt(maxValue) + " in a month");
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

    // Equirectangular, scaled by cos(latitude) so the wards keep their shape; only used to size the box.
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
    var H = Math.round(worldH * (W - pad * 2) / worldW + pad * 2);

    var unitText = mapMetric === "rate" ? "offenses per 1,000 residents" : "offenses";
    // Sized before the chart is created: ECharts measures its container once, at init.
    canvas.style.maxWidth = "560px";
    canvas.style.margin = "0.5rem auto 0";
    var chart = chartIn(canvas, H, "Map of Minneapolis council wards shaded by " + unitText + ", Jan 1 to " + monthDayLabel(data.through) + ". The ward list below has the same numbers.");
    if (!chart) return;

    echarts.registerMap("minneapolis-wards", {
      type: "FeatureCollection",
      features: wardGeo.map(function (f) {
        return { type: "Feature", geometry: f.geometry,
          properties: { name: "Ward " + f.properties.ward, cp: [f.properties.labelLng, f.properties.labelLat] } };
      })
    });

    var items = wardGeo.map(function (f) {
      var n = f.properties.ward, t = (valueOf(byWard[n]) - lo) / span, color = rampColor(t);
      return {
        name: "Ward " + n, ward: n, value: valueOf(byWard[n]), councilName: f.properties.name,
        itemStyle: { areaColor: color },
        label: { color: t > 0.55 ? "#fff" : INK },
        emphasis: { itemStyle: { areaColor: color, borderColor: INK, borderWidth: 2.5 }, label: { color: t > 0.55 ? "#fff" : INK } }
      };
    });

    chart.setOption({
      animation: false,
      tooltip: tooltipBox({
        trigger: "item",
        formatter: function (p) {
          var item = p.data, w = byWard[item.ward];
          var html = tipTitle("Ward " + item.ward + (item.councilName ? " (" + item.councilName + ")" : ""));
          if (w) {
            html += tipRow(fmt(w.counts[cur]), UNIT + " so far in " + years[cur]);
            if (w.ratePerThousand !== undefined && w.ratePerThousand !== null) html += tipRow(w.ratePerThousand.toLocaleString("en-US"), "per 1,000 residents");
            if (w.changeVsPrior !== null && w.changeVsPrior !== undefined) html += tipNote(formatChange(w.changeVsPrior) + " from " + years[cur - 1]);
          }
          return html;
        }
      }),
      series: [{
        type: "map", map: "minneapolis-wards", roam: false, selectedMode: false, cursor: "pointer",
        aspectScale: k, left: pad, right: pad, top: pad, bottom: pad,
        itemStyle: { borderColor: PAPER, borderWidth: 1.5 },
        label: { show: true, formatter: function (p) { return p.name.replace("Ward ", ""); }, fontFamily: FONT, fontSize: 12, fontWeight: 700 },
        data: items
      }]
    });
    chart.on("click", function (p) { if (p.data && p.data.ward) setArea("ward:" + p.data.ward); });

    scaleKey(canvas, label(lo), label(hi) + " " + (mapMetric === "rate" ? "per 1,000 residents" : "offenses"));
  }

  // ---------- hour by weekday ----------

  function renderHour() {
    var section = document.getElementById("chart-hour");
    if (!section || !data.hourWeekday) return;
    var canvas = canvasOf("chart-hour");

    var days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    var hourLabel = function (h) { return (h % 12 === 0 ? 12 : h % 12) + (h < 12 ? " AM" : " PM"); };
    var narrow = canvas.clientWidth < 520;

    tables.hour = {
      headers: ["Weekday"].concat(Array.apply(null, Array(24)).map(function (_, h) { return hourLabel(h); })),
      rows: days.map(function (day, di) { return [day].concat(data.hourWeekday[di]); })
    };

    var maxValue = 0;
    data.hourWeekday.forEach(function (row) { row.forEach(function (v) { if (v > maxValue) maxValue = v; }); });

    var cells = [];
    days.forEach(function (day, di) {
      for (var h = 0; h < 24; h++) {
        var v = data.hourWeekday[di][h];
        cells.push({ value: [h, di, v], itemStyle: { color: rampColor(maxValue ? v / maxValue : 0) } });
      }
    });

    drawHeatmap(canvas, {
      xs: Array.apply(null, Array(24)).map(function (_, h) { return h; }), ys: days, cells: cells,
      labelW: 38, top: 20, rowH: narrow ? 22 : 26, radius: 2, xInterval: 2,
      xFormatter: function (h) { return narrow ? String(h % 12 === 0 ? 12 : h % 12) + (h < 12 ? "a" : "p") : hourLabel(Number(h)); },
      aria: "Offenses by hour of day and weekday over the last 12 months, darker means more. The table below has the same numbers.",
      tip: function (cell) {
        var h = cell.value[0];
        return tipTitle(days[cell.value[1]] + ", " + hourLabel(h) + " to " + hourLabel((h + 1) % 24)) + tipRow(fmt(cell.value[2]), UNIT + ", last 12 months");
      }
    });
    if (window.echarts) scaleKey(canvas, "0", fmt(maxValue) + " in an hour slot");
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
    var cols = ["Group", String(years[cur]), String(years[cur - 1]), "Change from " + years[cur - 1]];
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
    var tail = "";
    if (vsPrior !== null) tail += ": " + formatChange(vsPrior) + " from " + years[cur - 1];
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
