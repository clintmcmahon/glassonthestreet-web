// Hover/focus/tap tooltips for the homepage's yearly trend charts. The
// server renders each year as a <g class="city-chart-year"> carrying its
// numbers in data attributes; this just positions one HTML tooltip per chart
// (HTML rather than SVG text so it stays readable when the SVG scales down
// on a phone).
(function () {
  document.querySelectorAll(".city-chart").forEach(function (chart) {
    var svg = chart.querySelector("svg");
    var years = chart.querySelectorAll(".city-chart-year");
    if (!svg || !years.length) return;

    var tip = document.createElement("div");
    tip.className = "city-chart-tooltip";
    tip.setAttribute("aria-hidden", "true");
    chart.appendChild(tip);

    var active = null;

    function line(className, text) {
      if (!text) return;
      var el = document.createElement("div");
      el.className = className;
      el.textContent = text;
      tip.appendChild(el);
    }

    function show(year) {
      if (active) active.classList.remove("is-active");
      active = year;
      year.classList.add("is-active");

      tip.textContent = "";
      line("city-chart-tooltip-title", year.dataset.tipTitle);
      line("city-chart-tooltip-value", year.dataset.tipValue);
      line("city-chart-tooltip-detail", year.dataset.tipDetail);
      tip.classList.add("is-visible");

      // Map the SVG anchor point (viewBox units) to pixels inside .city-chart.
      var scale = svg.getBoundingClientRect().width / svg.viewBox.baseVal.width;
      var svgBox = svg.getBoundingClientRect();
      var chartBox = chart.getBoundingClientRect();
      var ax = svgBox.left - chartBox.left + parseFloat(year.dataset.anchorX) * scale;
      var ay = svgBox.top - chartBox.top + parseFloat(year.dataset.anchorY) * scale;

      var w = tip.offsetWidth;
      var h = tip.offsetHeight;
      var left = Math.min(Math.max(ax - w / 2, 0), chartBox.width - w);
      var top = ay - h - 12;
      if (top < 0) top = ay + 14;
      tip.style.left = left + "px";
      tip.style.top = top + "px";
    }

    function hide() {
      if (active) active.classList.remove("is-active");
      active = null;
      tip.classList.remove("is-visible");
    }

    years.forEach(function (year) {
      year.addEventListener("pointerenter", function () { show(year); });
      year.addEventListener("pointerleave", function (e) {
        // Touch has no real "leave": keep it up until the next tap elsewhere.
        if (e.pointerType !== "touch") hide();
      });
      year.addEventListener("focus", function () { show(year); });
      year.addEventListener("blur", hide);
    });

    document.addEventListener("pointerdown", function (e) {
      if (!chart.contains(e.target)) hide();
    });
  });
})();
