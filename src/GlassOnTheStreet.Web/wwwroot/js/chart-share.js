// Share tools for chart cards: download a chart as a PNG (with title, legend and
// source line baked in) and copy an embed code for the same-period chart.
(function () {
  var NS = "http://www.w3.org/2000/svg";
  var INK = "#18181b", SOFT = "#71717a", PAPER = "#fafaf9";
  var STYLE_PROPS = ["fill", "stroke", "stroke-width", "stroke-dasharray", "stroke-linecap", "stroke-linejoin",
    "stroke-opacity", "fill-opacity", "opacity", "font-family", "font-size", "font-weight", "text-anchor", "display"];

  function el(tag, attrs, parent) {
    var node = document.createElementNS(NS, tag);
    Object.keys(attrs || {}).forEach(function (k) { node.setAttribute(k, attrs[k]); });
    if (parent) parent.appendChild(node);
    return node;
  }

  // An SVG drawn into an image can't see the page's CSS, so copy the computed
  // style of every element onto itself.
  function inlineStyles(source, target) {
    var cs = getComputedStyle(source);
    STYLE_PROPS.forEach(function (p) { target.style.setProperty(p, cs.getPropertyValue(p)); });
    for (var i = 0; i < source.children.length; i++) {
      if (target.children[i]) inlineStyles(source.children[i], target.children[i]);
    }
  }

  function legendEntries(card) {
    var entries = [];
    card.querySelectorAll(".trends-legend-item, .trends-legend-static").forEach(function (item) {
      if (item.classList.contains("is-off")) return;
      var key = item.querySelector(".trends-legend-key");
      var hollow = item.querySelector(".trends-legend-hollow");
      var color = key ? getComputedStyle(key).borderTopColor : null;
      if (key && color) entries.push({ color: color, text: item.textContent.trim(), hollow: false });
      else if (hollow) entries.push({ color: getComputedStyle(hollow).borderTopColor, text: item.textContent.trim(), hollow: true });
    });
    return entries;
  }

  function download(blob, name) {
    var url = URL.createObjectURL(blob);
    var link = document.createElement("a");
    link.href = url;
    link.download = name;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(function () { URL.revokeObjectURL(url); }, 2000);
  }

  function exportPng(card, button) {
    var svg = card.querySelector("svg.trends-svg");
    if (!svg) return;

    var vb = svg.viewBox && svg.viewBox.baseVal;
    var w = vb && vb.width ? vb.width : svg.getBoundingClientRect().width;
    var h = vb && vb.height ? vb.height : svg.getBoundingClientRect().height;
    var clone = svg.cloneNode(true);
    inlineStyles(svg, clone);
    clone.setAttribute("width", w);
    clone.setAttribute("height", h);
    clone.removeAttribute("style");
    clone.setAttribute("overflow", "visible");

    var title = (card.querySelector(".trends-card-title") || {}).textContent || "Minneapolis crime";

    // Say which slice the chart shows when a filter is active.
    var scope = [];
    [document.getElementById("filter-category"), document.getElementById("filter-area")].forEach(function (select) {
      if (select && select.value) scope.push(select.options[select.selectedIndex].textContent);
    });
    if (scope.length) title += " (" + scope.join(", ") + ")";

    // Keep the description, drop sentences that only make sense on the live page.
    var sub = ((card.querySelector(".trends-card-sub") || {}).textContent || "").trim()
      .split(/(?<=\.)\s+/).filter(function (sentence) { return !/\b(click|hover|tap)\b/i.test(sentence); }).join(" ");
    if (sub.length > 130) sub = sub.slice(0, 127) + "...";

    var pad = 24;
    var outW = Math.ceil(w + pad * 2);
    var entries = legendEntries(card);

    // Lay the legend out in rows that fit the image width.
    var rows = [], row = [], rowW = 0;
    entries.forEach(function (e) {
      var itemW = 30 + e.text.length * 6.6 + 14;
      if (rowW + itemW > outW - pad * 2 && row.length) { rows.push(row); row = []; rowW = 0; }
      row.push({ entry: e, x: rowW });
      rowW += itemW;
    });
    if (row.length) rows.push(row);

    var headerH = 28 + (sub ? 20 : 0) + rows.length * 22 + 12;
    var footerH = 36;
    var outH = Math.ceil(headerH + h + footerH + pad);

    var out = el("svg", { xmlns: NS, width: outW, height: outH, viewBox: "0 0 " + outW + " " + outH });
    el("rect", { x: 0, y: 0, width: outW, height: outH, fill: PAPER }, out);
    var t = el("text", { x: pad, y: pad + 12, "font-family": "Manrope, Arial, sans-serif", "font-size": 17, "font-weight": 700, fill: INK }, out);
    t.textContent = title;
    var y = pad + 12;
    if (sub) {
      y += 20;
      var st = el("text", { x: pad, y: y, "font-family": "Manrope, Arial, sans-serif", "font-size": 12, fill: SOFT }, out);
      st.textContent = sub;
    }
    rows.forEach(function (r) {
      y += 22;
      r.forEach(function (item) {
        var x = pad + item.x;
        if (item.entry.hollow) {
          el("circle", { cx: x + 8, cy: y - 4, r: 4.5, fill: PAPER, stroke: item.entry.color, "stroke-width": 2.5 }, out);
        } else {
          el("line", { x1: x, x2: x + 18, y1: y - 4, y2: y - 4, stroke: item.entry.color, "stroke-width": 3, "stroke-linecap": "round" }, out);
        }
        var lt = el("text", { x: x + 24, y: y, "font-family": "Manrope, Arial, sans-serif", "font-size": 12, fill: INK }, out);
        lt.textContent = item.entry.text;
      });
    });

    var g = el("g", { transform: "translate(" + pad + "," + headerH + ")" }, out);
    g.appendChild(clone);

    var source = el("text", { x: pad, y: outH - pad + 2, "font-family": "Manrope, Arial, sans-serif", "font-size": 11, fill: SOFT }, out);
    source.textContent = "Source: Minneapolis Police Department open data, via " + location.host + location.pathname;

    var xml = new XMLSerializer().serializeToString(out);
    var img = new Image();
    var scale = 2;
    var label = button.textContent;
    button.disabled = true;
    button.textContent = "Preparing...";
    img.onload = function () {
      var canvas = document.createElement("canvas");
      canvas.width = outW * scale;
      canvas.height = outH * scale;
      var ctx = canvas.getContext("2d");
      ctx.scale(scale, scale);
      ctx.drawImage(img, 0, 0, outW, outH);
      canvas.toBlob(function (blob) {
        if (blob) download(blob, (card.id || "chart") + "-glass-on-the-street.png");
        button.disabled = false;
        button.textContent = label;
      }, "image/png");
    };
    img.onerror = function () { button.disabled = false; button.textContent = label; };
    img.src = "data:image/svg+xml;charset=utf-8," + encodeURIComponent(xml);
  }

  function embedUrl(card) {
    if (card.dataset.embedSrc) return card.dataset.embedSrc;
    // On the dashboards the current filters live in the page address.
    var params = new URLSearchParams(location.search);
    var out = new URLSearchParams({ scope: card.dataset.embedScope || "crime" });
    ["category", "neighborhood", "ward"].forEach(function (k) { if (params.get(k)) out.set(k, params.get(k)); });
    return "/embed/year-bars?" + out.toString();
  }

  function addEmbed(card, actions) {
    var details = document.createElement("details");
    details.className = "share-embed";
    var summary = document.createElement("summary");
    summary.textContent = "Embed this chart";
    details.appendChild(summary);
    var area = document.createElement("textarea");
    area.readOnly = true;
    details.appendChild(area);
    var copy = document.createElement("button");
    copy.type = "button";
    copy.className = "btn-outline trends-download";
    copy.textContent = "Copy embed code";
    copy.style.marginTop = "0.4rem";
    details.appendChild(copy);

    function fill() {
      var title = (card.querySelector(".trends-card-title") || {}).textContent || "Minneapolis crime chart";
      area.value = '<iframe src="' + location.origin + embedUrl(card) + '" width="640" height="440" ' +
        'style="border:0;max-width:100%" loading="lazy" title="' + title.replace(/"/g, "") + '"></iframe>';
    }
    details.addEventListener("toggle", function () { if (details.open) fill(); });
    copy.addEventListener("click", function () {
      fill();
      area.select();
      (navigator.clipboard ? navigator.clipboard.writeText(area.value) : Promise.reject()).then(
        function () { copy.textContent = "Copied"; setTimeout(function () { copy.textContent = "Copy embed code"; }, 1500); },
        function () { document.execCommand("copy"); });
    });
    actions.parentNode.insertBefore(details, actions.nextSibling);
  }

  document.querySelectorAll(".trends-card").forEach(function (card) {
    var isChart = card.matches("#chart-yoy, #chart-period, #chart-heat, #chart-hour") || card.querySelector("svg.trends-svg");
    if (!isChart && !card.dataset.embed) return;

    var head = card.querySelector(".trends-card-head");
    var csv = card.querySelector("[data-download]");
    var actions = document.createElement("div");
    actions.className = "share-actions";

    if (head && csv) {
      head.appendChild(actions);
      actions.appendChild(csv);
    } else if (head) {
      head.appendChild(actions);
    } else {
      var anchor = card.querySelector(".trends-card-sub") || card.querySelector(".trends-card-title");
      anchor.parentNode.insertBefore(actions, anchor.nextSibling);
    }

    if (isChart) {
      var png = document.createElement("button");
      png.type = "button";
      png.className = "btn-outline trends-download";
      png.textContent = "Download PNG";
      png.addEventListener("click", function () { exportPng(card, png); });
      actions.appendChild(png);
    }

    if (card.dataset.embed) addEmbed(card, head ? head : actions);
  });
})();
