/* =============================================================================
   dashboard.js — Chart.js initialization for the redesigned Tak Makaron
   admin dashboard. Renders 4 charts:

     1. Revenue Trend (Line, 2 datasets — this week + last week)
     2. Orders by Status (Donut, 4 slices)
     3. Top Products (Horizontal Bar, top 7)
     4. Category Distribution (Pie, top 5 + Others = 6 slices)

   EXPOSES:
     window.takDashboard.render(data)
       Renders all 4 charts using the supplied data object. Safe to call
       multiple times — existing charts are destroyed before re-creating.

   DATA SHAPE (matches DashboardStatsDto):
     {
       isToman: true,
       displayCurrency: "تومان",
       thisWeekRevenue: [{ dayLabel, totalAmount }, ...],
       lastWeekRevenue: [{ dayLabel, totalAmount }, ...],
       statusBreakdown: [{ status, count }, ...],
       topProducts: [{ productName, quantitySold, totalAmount }, ...],
       topCategories: [{ categoryName, salesCount }, ...],
       weeklyTotal: { avg, max, growthPct }
     }

   PERSIAN DIGITS:
     Numeric labels in tooltips and axis ticks follow the current UI culture:
     Persian digits (۰-۹) when <html lang="fa"> (fa-IR mode), ASCII digits
     (0-9) when <html lang="en"> (en-US mode). The isFa() helper reads
     document.documentElement.lang once at render time — set by App.razor
     from CultureInfo.CurrentUICulture so it's always in sync with the
     server-side culture.

   CURRENCY FORMATTING:
     When isToman is true, amounts are already in Toman (server divided by 10).
     Tooltip format (fa-IR): " ۲٫۴M تومان" for millions, " ۱۴٬۰۰۰ تومان" for thousands.
     Tooltip format (en-US): " 2.4M toman" / " 14,000 toman".
     When isToman is false, the original currency code is used (e.g. "USD").
   ========================================================================== */

(function () {
    'use strict';

    // ---- Theme detection (tak-light vs tak-dark) ----
    // theme.js sets the `data-theme-name` attribute on <html> to the
    // active theme string ("tak-light", "tak-dark", "standard-dark",
    // etc.) on every theme switch. We read it here at render time so
    // chart text colors can adapt to the active theme.
    //
    // WHY THIS EXISTS:
    //   Previously the Top Products bar chart had its Y-axis labels
    //   (product names) hard-coded to #1F2937 (dark gray) — readable
    //   on tak-light's white card background but INVISIBLE on tak-dark's
    //   dark card background. The same problem affected grid lines
    //   (rgba(255,255,255,0.04) is invisible on white) and donut/pie
    //   slice borders (#152019 blends into a white card). All of these
    //   now flow through theme-aware helpers below.
    //
    // FALLBACK: if the attribute isn't set yet (very early paint before
    // theme.js has run), fall back to localStorage — same source theme.js
    // reads from.
    function isDarkTheme() {
        var name = (document.documentElement.getAttribute('data-theme-name') || '').toLowerCase();
        if (!name) {
            try {
                name = (localStorage.getItem('radzen_theme') || '').toLowerCase();
            } catch (e) {
                name = '';
            }
        }
        // 'tak-dark', 'standard-dark', 'dark', etc. all contain 'dark'.
        // Default to light when unknown — the page default is tak-light.
        return name.indexOf('dark') !== -1;
    }

    // ---- Theme-aware chart color tokens ----
    // Centralized so every chart picks up the right palette for the
    // active theme. Add new tokens here, not inline in chart code.
    //
    // DARK (tak-dark / standard-dark):
    //   - tickColor: #94A3A0 — light slate (existing, readable on dark)
    //   - gridLine : rgba(255,255,255,0.04) — faint white (existing)
    //   - sliceBorder: #152019 — near-black separator (existing)
    //   - emptyState: #94A3A0 — light slate (existing)
    //
    // LIGHT (tak-light / standard / material):
    //   - tickColor: #1F2937 — slate-900 (readable on white card)
    //   - gridLine : rgba(15,23,42,0.06) — faint slate (visible on white)
    //   - sliceBorder: #FFFFFF — white separator (visible on light card)
    //   - emptyState: #475569 — slate-600 (readable on white)
    function axisTickColor() { return isDarkTheme() ? '#94A3A0' : '#1F2937'; }
    function gridLineColor() { return isDarkTheme() ? 'rgba(255,255,255,0.04)' : 'rgba(15,23,42,0.06)'; }
    function sliceBorderColor() { return isDarkTheme() ? '#152019' : '#FFFFFF'; }
    function emptyStateColor() { return isDarkTheme() ? '#94A3A0' : '#475569'; }

    // Chart.js global defaults — theme-aware.
    // applyChartDefaults() runs on every render (including re-renders
    // triggered by the theme MutationObserver below), so the defaults
    // always match the current theme.
    function applyChartDefaults() {
        if (typeof Chart === 'undefined') return;
        var dark = isDarkTheme();
        Chart.defaults.color = dark ? '#94A3A0' : '#475569';
        Chart.defaults.borderColor = dark ? 'rgba(255,255,255,0.05)' : 'rgba(15,23,42,0.06)';
        Chart.defaults.font.family = "'Vazirmatn', 'Noto Sans SC', sans-serif";
        Chart.defaults.font.size = 11;
    }

    // ---- Culture detection (fa-IR → Persian digits, en-US → ASCII) ----
    // The <html lang="..."> attribute is set server-side by App.razor from
    // CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, so this is
    // always in sync with the culture the user picked.
    function isFa() {
        var lang = (document.documentElement.lang || '').toLowerCase();
        return lang.indexOf('fa') === 0;
    }

    // ---- Localized digits helper ----
    // Converts ASCII 0-9 to Persian ۰-۹ ONLY when the current UI culture is
    // fa-IR. In en-US mode, leaves digits unchanged. Non-digit characters
    // are always left untouched.
    function toLocalDigits(n) {
        var s = String(n);
        if (!isFa()) return s;
        return s.replace(/[0-9]/g, function (d) {
            return '۰۱۲۳۴۵۶۷۸۹'[d];
        });
    }

    // ---- Coerce a localized-label field to a string -----------------------
    // The razor page passes localized labels (labelThisWeek, labelPending,
    // etc.) via JS interop. If the .NET side forgot to extract `.Value` from
    // IStringLocalizer["..."], System.Text.Json serializes the LocalizedString
    // STRUCT (with { name, value, resourceNotFound, searchedLocation } fields)
    // instead of the underlying string. Concatenating that struct with strings
    // in a tooltip callback then produces the literal "[object Object]" text
    // in the rendered tooltip.
    //
    // Defensive helper: if we receive an object that looks like a
    // LocalizedString (has a `value` string property), unwrap it. Otherwise
    // return the value as-is. This way the dashboard keeps working even if a
    // future razor change forgets `.Value` on a new label.
    function asStr(v, fallback) {
        if (typeof v === 'string') return v;
        if (v && typeof v === 'object' && typeof v.value === 'string') return v.value;
        return fallback != null ? fallback : '';
    }

    // ---- Format amount with grouping (culture-aware) ----
    // fa-IR: Persian digits + Persian thousands separator (٬)
    // en-US: ASCII digits + comma separator (,)
    function formatAmount(amount, isToman) {
        var rounded = Math.round(amount);
        var grouped = rounded.toLocaleString('en-US');
        if (!isFa()) return grouped;
        return grouped.replace(/,/g, '٬').replace(/[0-9]/g, function (d) {
            return '۰۱۲۳۴۵۶۷۸۹'[d];
        });
    }

    // ---- Format millions (for line chart Y-axis + tooltip) ----
    // fa-IR: "۲٫۴M تومان"  /  en-US: "2.4M toman"
    function formatMillions(amount, isToman, currencyLabel) {
        var millions = amount / 1000000;
        var formatted = millions.toFixed(2);
        if (isFa()) {
            formatted = formatted.replace(/[0-9]/g, function (d) {
                return '۰۱۲۳۴۵۶۷۸۹'[d];
            }).replace('.', '٫');
        }
        return formatted + 'M ' + currencyLabel;
    }

    // ---- Common tooltip style for all charts ----
    // Tooltips stay dark Tak-green on BOTH themes. Rationale: tooltips
    // are floating popups that don't share the card background, so a
    // dark popup is readable on both light and dark themes. Keeping the
    // brand color (dark Tak green + gold border) consistent across
    // themes is also a deliberate brand decision — the popup is the
    // one place the Tak Makaron accent always shows through.
    function commonTooltip() {
        return {
            backgroundColor: '#0A2A1F',
            titleColor: '#E8EDE9',
            bodyColor: '#94A3A0',
            borderColor: 'rgba(212, 175, 55, 0.3)',
            borderWidth: 1,
            padding: 10,
            cornerRadius: 8,
            displayColors: true,
            titleFont: { weight: '700' },
            bodyFont: { size: 11 }
        };
    }

    // ---- Chart instances (so we can destroy before re-render) ----
    var charts = {};

    // Last data handed to render(). Kept so the theme MutationObserver
    // (set up at the bottom of this IIFE) can re-render all charts with
    // the new theme's colors without needing the razor page to re-call
    // takDashboard.render — the razor page only invokes render() once
    // (on first stats load), so without this cache a runtime theme
    // switch would leave charts stuck on the old theme's colors.
    var _lastData = null;

    function destroyCharts() {
        Object.keys(charts).forEach(function (key) {
            if (charts[key]) {
                charts[key].destroy();
                charts[key] = null;
            }
        });
    }

    // ---- 1) Revenue Trend (Line, two datasets) ----
    function renderRevenueChart(data) {
        var canvas = document.getElementById('tm-dash-revenue-chart');
        if (!canvas || typeof Chart === 'undefined') return;

        var thisWeekArr = data.thisWeekRevenue || [];
        var lastWeekArr = data.lastWeekRevenue || [];

        // Empty-state: if both weeks are entirely zero, the line chart would
        // render as a flat line at y=0 — not blank, but uninformative. We
        // only treat it as "empty" if there are NO data points at all.
        if (thisWeekArr.length === 0) {
            renderEmptyState(canvas, asStr(data.labelNoData, 'هنوز داده‌ای موجود نیست'));
            return;
        }

        var ctx = canvas.getContext('2d');
        var gradient = ctx.createLinearGradient(0, 0, 0, 260);
        gradient.addColorStop(0, 'rgba(42, 157, 126, 0.35)');
        gradient.addColorStop(1, 'rgba(42, 157, 126, 0)');

        var thisWeekLabels = thisWeekArr.map(function (d) { return d.dayLabel; });
        var thisWeekData = thisWeekArr.map(function (d) { return d.totalAmount; });
        var lastWeekData = lastWeekArr.map(function (d) { return d.totalAmount; });

        // asStr() unwraps LocalizedString structs in case the razor page
        // forgets `.Value` on a label field — see the asStr() comment above.
        var currencyLabel = asStr(data.displayCurrency, 'تومان');
        var labelThisWeek = asStr(data.labelThisWeek, 'این هفته');
        var labelLastWeek = asStr(data.labelLastWeek, 'هفته قبل');

        charts.revenue = new Chart(ctx, {
            type: 'line',
            data: {
                labels: thisWeekLabels,
                datasets: [
                    {
                        label: labelThisWeek,
                        data: thisWeekData,
                        borderColor: '#2A9D7E',
                        backgroundColor: gradient,
                        borderWidth: 2.5,
                        fill: true,
                        tension: 0.4,
                        pointBackgroundColor: '#2A9D7E',
                        pointBorderColor: '#0E1A14',
                        pointBorderWidth: 2,
                        pointRadius: 4,
                        pointHoverRadius: 6
                    },
                    {
                        label: labelLastWeek,
                        data: lastWeekData,
                        borderColor: 'rgba(212, 175, 55, 0.55)',
                        borderWidth: 2,
                        borderDash: [6, 4],
                        fill: false,
                        tension: 0.4,
                        pointRadius: 0,
                        pointHoverRadius: 4,
                        pointBackgroundColor: '#D4AF37'
                    }
                ]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    legend: { display: false },
                    tooltip: Object.assign({}, commonTooltip(), {
                        callbacks: {
                            label: function (ctx) {
                                // asStr() guards against `ctx.dataset.label`
                                // being a LocalizedString struct (it would be
                                // if the dataset label came from data.label*
                                // without .Value extraction on the razor side).
                                return ' ' + asStr(ctx.dataset.label, '') + ': ' +
                                    formatMillions(ctx.parsed.y, data.isToman, currencyLabel);
                            }
                        }
                    })
                },
                scales: {
                    x: { grid: { display: false }, ticks: { color: axisTickColor() } },
                    y: {
                        grid: { color: gridLineColor() },
                        ticks: {
                            color: axisTickColor(),
                            callback: function (v) {
                                var millions = v / 1000000;
                                if (millions === 0) return toLocalDigits('0');
                                return toLocalDigits(millions.toFixed(1)) + 'M';
                            }
                        },
                        beginAtZero: true
                    }
                }
            }
        });
    }

    // ---- 2) Orders by Status (Donut) ----
    function renderStatusDonut(data) {
        var canvas = document.getElementById('tm-dash-status-donut');
        if (!canvas || typeof Chart === 'undefined') return;

        var statusData = data.statusBreakdown || [];

        // Empty-state: no statuses (no sales in scope at all).
        if (statusData.length === 0) {
            renderEmptyState(canvas, asStr(data.labelNoData, 'هنوز داده‌ای موجود نیست'));
            return;
        }

        // Map status names to localized labels + colors
        // Order matches the HTML reference: Approved (green), Shipped/Invoiced (blue),
        // Pending (orange), Cancelled (red). Draft is omitted if count is 0.
        // asStr() unwraps LocalizedString structs in case the razor page
        // forgets `.Value` on a label field.
        var labelOrdersUnit = asStr(data.labelOrdersUnit, 'سفارش');
        var colorMap = {
            'Approved': { color: '#2A9D7E', label: asStr(data.labelApproved, 'تأیید شده') },
            'Invoiced': { color: '#5BA8E8', label: asStr(data.labelShipped, 'ارسال شده') },
            'Pending': { color: '#E8A14A', label: asStr(data.labelPending, 'در انتظار') },
            'Cancelled': { color: '#EF6B6B', label: asStr(data.labelCancelled, 'لغو شده') },
            'Draft': { color: '#94A3A0', label: asStr(data.labelDraft, 'پیش‌نویس') }
        };

        var labels = statusData.map(function (s) {
            return colorMap[s.status] ? colorMap[s.status].label : s.status;
        });
        var values = statusData.map(function (s) { return s.count; });
        var colors = statusData.map(function (s) {
            return colorMap[s.status] ? colorMap[s.status].color : '#94A3A0';
        });

        charts.statusDonut = new Chart(canvas.getContext('2d'), {
            type: 'doughnut',
            data: {
                labels: labels,
                datasets: [{
                    data: values,
                    backgroundColor: colors,
                    borderColor: sliceBorderColor(),
                    borderWidth: 3,
                    hoverOffset: 6
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                cutout: '72%',
                plugins: {
                    legend: { display: false },
                    tooltip: Object.assign({}, commonTooltip(), {
                        callbacks: {
                            label: function (ctx) {
                                // ctx.label may be a LocalizedString struct if
                                // the labels array was built from data.label*
                                // without .Value extraction — asStr() unwraps it.
                                return ' ' + asStr(ctx.label, '') + ': ' + toLocalDigits(ctx.parsed) +
                                    ' ' + labelOrdersUnit;
                            }
                        }
                    })
                }
            }
        });
    }

    // ---- Render an "empty state" message on a canvas ----------------------
    // Used when a chart's data array is empty (e.g. no sales in scope).
    // Without this, Chart.js renders a blank white canvas with no axes,
    // which looks like a broken chart rather than "no data yet".
    function renderEmptyState(canvas, message) {
        var ctx = canvas.getContext('2d');
        // Clear any prior drawing
        ctx.clearRect(0, 0, canvas.width, canvas.height);
        // Theme-aware fill — must be readable on both tak-light (white
        // card) and tak-dark (dark card). Previously hard-coded #94A3A0
        // which was faint on white.
        ctx.fillStyle = emptyStateColor();
        ctx.font = '13px "Vazirmatn", "Noto Sans SC", sans-serif';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        // Center the text in the canvas's CSS-rendered box (use clientWidth/
        // clientHeight rather than the canvas attribute width/height, since
        // the latter can be 0 before Chart.js has touched the canvas).
        var w = canvas.clientWidth || canvas.width || 300;
        var h = canvas.clientHeight || canvas.height || 150;
        ctx.fillText(message, w / 2, h / 2);
    }

    // ---- 3) Top Products (Horizontal Bar — by TOTAL SALES AMOUNT) ----
    // X-axis plots revenue per product (not quantity). Ticks are formatted
    // as money (no decimals): "۲٫۴M" for millions, "۱۴٬۰۰۰" for thousands.
    // Y-axis labels (product names) flow through axisTickColor() — dark
    // slate on tak-light's white card, light slate on tak-dark's dark
    // card. Previously hard-coded to a single color, which was invisible
    // on one theme or the other.
    function renderTopProductsChart(data) {
        var canvas = document.getElementById('tm-dash-top-products-chart');
        if (!canvas || typeof Chart === 'undefined') return;

        var products = data.topProducts || [];

        // Empty-state: no products to plot. Show a friendly localized
        // message instead of a blank canvas.
        if (products.length === 0) {
            renderEmptyState(canvas, asStr(data.labelNoData, 'هنوز داده‌ای موجود نیست'));
            return;
        }

        var labels = products.map(function (p) { return p.productName; });
        // Plot TotalAmount (revenue) instead of QuantitySold. The server
        // already converted IRR→Toman, so values are in display currency.
        var values = products.map(function (p) { return p.totalAmount; });

        // Build the gradient ONCE from the canvas's own 2D context, before
        // passing it to Chart.js. The previous version used a scriptable
        // function `backgroundColor: function(ctx) { ... ctx.chart.ctx ... }`
        // which is called by Chart.js internally during its render cycle —
        // if the scriptable context shape differs across Chart.js versions
        // (or ctx.chart.ctx is not yet available), the function throws inside
        // Chart.js and aborts the chart's render, leaving a blank canvas.
        // Pre-computing the gradient here is simpler and avoids the scriptable
        // context entirely.
        var ctx2d = canvas.getContext('2d');
        var gradient = null;
        try {
            gradient = ctx2d.createLinearGradient(0, 0, 600, 0);
            gradient.addColorStop(0, 'rgba(42, 157, 126, 0.9)');
            gradient.addColorStop(1, 'rgba(212, 175, 55, 0.9)');
        } catch (e) {
            // If gradient creation fails for any reason, fall back to a
            // solid color so the bars are still visible.
            gradient = 'rgba(42, 157, 126, 0.9)';
        }

        var currencyLabel = asStr(data.displayCurrency, 'تومان');

        // ---- X-axis tick formatter ----------------------------------------
        // Chart.js auto-picks "nice" tick values, which on a quantity axis
        // produced fractional ticks like 2.5, 7.5 (the user's reported
        // "decimal numbers on the x axis"). Now that we plot money, we also
        // format each tick as a rounded money value:
        //   0           → "0"
        //   < 1,000,000 → grouped thousands ("۱۴٬۰۰۰")
        //   ≥ 1,000,000 → "X.XM" ("۲٫۴M")
        // All non-zero values are integers (no decimals on the axis).
        function formatAxisMoney(v) {
            var n = Number(v) || 0;
            if (n === 0) return toLocalDigits('0');
            var abs = Math.abs(n);
            if (abs >= 1000000) {
                var m = (n / 1000000).toFixed(1);
                // Drop trailing ".0" so "2.0M" becomes "2M" — cleaner axis.
                if (/\.0$/.test(m)) m = m.slice(0, -2);
                return toLocalDigits(m) + 'M';
            }
            // Grouped thousands, no decimals.
            return toLocalDigits(Math.round(n).toLocaleString('en-US'));
        }

        charts.topProducts = new Chart(ctx2d, {
            type: 'bar',
            data: {
                labels: labels,
                datasets: [{
                    label: asStr(data.labelSalesCount, 'تعداد فروش'),
                    data: values,
                    backgroundColor: gradient,
                    borderRadius: 6,
                    borderSkipped: false,
                    barThickness: 18
                }]
            },
            options: {
                indexAxis: 'y',
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    legend: { display: false },
                    tooltip: Object.assign({}, commonTooltip(), {
                        callbacks: {
                            label: function (ctx) {
                                // Show full grouped amount + currency label
                                // (more space in tooltip than on the axis).
                                return ' ' + formatAmount(ctx.parsed.x, data.isToman) +
                                    ' ' + currencyLabel;
                            }
                        }
                    })
                },
                scales: {
                    x: {
                        grid: { color: gridLineColor() },
                        ticks: {
                            color: axisTickColor(),
                            // Format every auto-picked tick as money — no
                            // more "2.5" / "7.5" decimals on the axis.
                            callback: function (v) { return formatAxisMoney(v); }
                        },
                        beginAtZero: true
                    },
                    y: {
                        grid: { display: false },
                        // Product names — theme-aware. Was hard-coded to
                        // #1F2937 (dark slate) which was readable on the
                        // tak-light (white) card but INVISIBLE on tak-dark's
                        // dark card. Now picks #1F2937 on light, #94A3A0
                        // (light slate) on dark — readable on both. The
                        // medium font weight is preserved for emphasis.
                        ticks: { color: axisTickColor(), font: { weight: '500' } }
                    }
                }
            }
        });
    }

    // ---- 4) Category Distribution (Pie, top 5 + Others) ----
    // Distinct colors per the user's spec — 6 slices, no two close to each other.
    var categoryColors = [
        '#2A9D7E',  // green (top 1)
        '#D4AF37',  // gold  (top 2)
        '#5BA8E8',  // blue  (top 3)
        '#E8A14A',  // orange(top 4)
        '#9B59B6',  // purple(top 5)  — added per "distinct colors" requirement
        '#EF6B6B'   // red   (Others) — alpha 0.7 was in HTML; here full to keep distinct from orange
    ];

    function renderCategoryPie(data) {
        var canvas = document.getElementById('tm-dash-category-pie');
        if (!canvas || typeof Chart === 'undefined') return;

        var cats = data.topCategories || [];

        // Empty-state: no categories to plot. Show a friendly localized
        // message instead of a blank canvas.
        if (cats.length === 0) {
            renderEmptyState(canvas, asStr(data.labelNoData, 'هنوز داده‌ای موجود نیست'));
            return;
        }

        var labels = cats.map(function (c) { return c.categoryName; });
        var values = cats.map(function (c) { return c.salesCount; });

        // Compute total for percentage display in tooltip
        var total = values.reduce(function (sum, v) { return sum + v; }, 0);

        charts.categoryPie = new Chart(canvas.getContext('2d'), {
            type: 'pie',
            data: {
                labels: labels,
                datasets: [{
                    data: values,
                    backgroundColor: categoryColors.slice(0, labels.length),
                    borderColor: sliceBorderColor(),
                    borderWidth: 3,
                    hoverOffset: 8
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    legend: { display: false },
                    tooltip: Object.assign({}, commonTooltip(), {
                        callbacks: {
                            label: function (ctx) {
                                var pct = total > 0 ? Math.round(ctx.parsed * 100 / total) : 0;
                                var pctSymbol = isFa() ? '٪' : '%';
                                // asStr() guards against ctx.label being a
                                // LocalizedString struct (it isn't, since
                                // category names come from the database, but
                                // the helper is harmless and consistent).
                                return ' ' + asStr(ctx.label, '') + ': ' + toLocalDigits(ctx.parsed) +
                                    ' (' + toLocalDigits(pct) + pctSymbol + ')';
                            }
                        }
                    })
                }
            }
        });
    }

    // ---- Render all 4 charts from a data payload -----------------------
    // Shared by the public render() API (window.takDashboard.render) and
    // the theme MutationObserver below. The observer needs to re-render
    // when the user switches themes at runtime, but the razor page only
    // calls render() once (on first stats load) — so the observer reads
    // _lastData (cached by render()) and calls renderAll() directly.
    function renderAll(data) {
        applyChartDefaults();
        destroyCharts();
        // Each chart is wrapped in its own try/catch so a failure in one
        // chart (e.g. a scriptable-context throw, a bad canvas, etc.)
        // does NOT abort the remaining charts. Previously a throw in
        // renderTopProductsChart would prevent renderCategoryPie from
        // running, leaving BOTH the bar and pie blank while the line
        // and donut (rendered first) worked fine.
        var renderers = [
            ['revenue', renderRevenueChart],
            ['statusDonut', renderStatusDonut],
            ['topProducts', renderTopProductsChart],
            ['categoryPie', renderCategoryPie]
        ];
        for (var i = 0; i < renderers.length; i++) {
            var name = renderers[i][0];
            var fn = renderers[i][1];
            try {
                fn(data);
            } catch (e) {
                // Log to console so the failure is visible in DevTools
                // instead of silently leaving a blank chart area.
                if (typeof console !== 'undefined' && console.error) {
                    console.error('[dashboard.js] render("' + name + '") failed:', e);
                }
            }
        }
    }

    window.takDashboard = {
        render: function (data) {
            _lastData = data;
            renderAll(data);
        },
        formatAmount: formatAmount,
        formatMillions: formatMillions
    };

    // ---- Runtime theme switch → re-render charts -------------------------
    // The dashboard only invokes takDashboard.render() ONCE (on first
    // stats load). After that, if the user clicks the sun/moon toggle in
    // the header, theme.js swaps the CSS but the chart colors stay stuck
    // on the old theme — axis labels become invisible on the new theme.
    //
    // We watch <html> for changes to the `data-theme-name` attribute
    // (which theme.js sets on every switch) and re-render all 4 charts
    // with the new theme's colors. Without this, switching from light
    // to dark would leave product names dark-on-dark; switching dark
    // to light would leave them light-on-light.
    //
    // _lastData is null until the first render() call, so the observer
    // is a no-op until the dashboard has actually loaded data.
    if (typeof MutationObserver !== 'undefined') {
        var themeObserver = new MutationObserver(function (mutations) {
            if (!_lastData) return;
            for (var i = 0; i < mutations.length; i++) {
                if (mutations[i].attributeName === 'data-theme-name') {
                    try {
                        renderAll(_lastData);
                    } catch (e) {
                        if (typeof console !== 'undefined' && console.error) {
                            console.error('[dashboard.js] theme re-render failed:', e);
                        }
                    }
                    return;
                }
            }
        });
        themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme-name'] });
    }
})();