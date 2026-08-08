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
       topProducts: [{ productName, quantitySold }, ...],
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

    // Chart.js global defaults — dark Tak Makaron palette
    function applyChartDefaults() {
        if (typeof Chart === 'undefined') return;
        Chart.defaults.color = '#94A3A0';
        Chart.defaults.borderColor = 'rgba(255,255,255,0.05)';
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
            renderEmptyState(canvas, data.labelNoData || 'هنوز داده‌ای موجود نیست');
            return;
        }

        var ctx = canvas.getContext('2d');
        var gradient = ctx.createLinearGradient(0, 0, 0, 260);
        gradient.addColorStop(0, 'rgba(42, 157, 126, 0.35)');
        gradient.addColorStop(1, 'rgba(42, 157, 126, 0)');

        var thisWeekLabels = thisWeekArr.map(function (d) { return d.dayLabel; });
        var thisWeekData = thisWeekArr.map(function (d) { return d.totalAmount; });
        var lastWeekData = lastWeekArr.map(function (d) { return d.totalAmount; });

        var currencyLabel = data.displayCurrency || 'تومان';

        charts.revenue = new Chart(ctx, {
            type: 'line',
            data: {
                labels: thisWeekLabels,
                datasets: [
                    {
                        label: data.labelThisWeek || 'این هفته',
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
                        label: data.labelLastWeek || 'هفته قبل',
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
                                return ' ' + ctx.dataset.label + ': ' +
                                    formatMillions(ctx.parsed.y, data.isToman, currencyLabel);
                            }
                        }
                    })
                },
                scales: {
                    x: { grid: { display: false }, ticks: { color: '#94A3A0' } },
                    y: {
                        grid: { color: 'rgba(255,255,255,0.04)' },
                        ticks: {
                            color: '#94A3A0',
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
            renderEmptyState(canvas, data.labelNoData || 'هنوز داده‌ای موجود نیست');
            return;
        }

        // Map status names to localized labels + colors
        // Order matches the HTML reference: Approved (green), Shipped/Invoiced (blue),
        // Pending (orange), Cancelled (red). Draft is omitted if count is 0.
        var colorMap = {
            'Approved': { color: '#2A9D7E', label: data.labelApproved || 'تأیید شده' },
            'Invoiced': { color: '#5BA8E8', label: data.labelShipped || 'ارسال شده' },
            'Pending': { color: '#E8A14A', label: data.labelPending || 'در انتظار' },
            'Cancelled': { color: '#EF6B6B', label: data.labelCancelled || 'لغو شده' },
            'Draft': { color: '#94A3A0', label: data.labelDraft || 'پیش‌نویس' }
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
                    borderColor: '#152019',
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
                                return ' ' + ctx.label + ': ' + toLocalDigits(ctx.parsed) +
                                    ' ' + (data.labelOrdersUnit || 'سفارش');
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
        ctx.fillStyle = '#94A3A0';
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

    // ---- 3) Top Products (Horizontal Bar) ----
    function renderTopProductsChart(data) {
        var canvas = document.getElementById('tm-dash-top-products-chart');
        if (!canvas || typeof Chart === 'undefined') return;

        var products = data.topProducts || [];

        // Empty-state: no products to plot. Show a friendly localized
        // message instead of a blank canvas.
        if (products.length === 0) {
            renderEmptyState(canvas, data.labelNoData || 'هنوز داده‌ای موجود نیست');
            return;
        }

        var labels = products.map(function (p) { return p.productName; });
        var values = products.map(function (p) { return p.quantitySold; });

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

        charts.topProducts = new Chart(ctx2d, {
            type: 'bar',
            data: {
                labels: labels,
                datasets: [{
                    label: data.labelSalesCount || 'تعداد فروش',
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
                                return ' ' + toLocalDigits(ctx.parsed.x) + ' ' +
                                    (data.labelSoldUnit || 'عدد فروخته شده');
                            }
                        }
                    })
                },
                scales: {
                    x: {
                        grid: { color: 'rgba(255,255,255,0.04)' },
                        ticks: { color: '#94A3A0', callback: function (v) { return toLocalDigits(v); } },
                        beginAtZero: true
                    },
                    y: {
                        grid: { display: false },
                        ticks: { color: '#E8EDE9', font: { weight: '500' } }
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
            renderEmptyState(canvas, data.labelNoData || 'هنوز داده‌ای موجود نیست');
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
                    borderColor: '#152019',
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
                                return ' ' + ctx.label + ': ' + toLocalDigits(ctx.parsed) +
                                    ' (' + toLocalDigits(pct) + pctSymbol + ')';
                            }
                        }
                    })
                }
            }
        });
    }

    // ---- Public API ----
    // NOTE: only `render` is called (by Dashboard.razor via JS interop).
    // formatAmount / formatMillions are exposed for potential external use;
    // both reference defined functions so they're safe.
    // (A previous version also exported `toFa: toFa` here, but `toFa` was
    // never defined — only `toLocalDigits` is. Referencing an undefined
    // identifier in an object literal throws ReferenceError at IIFE run
    // time, which aborted this whole IIFE BEFORE `window.takDashboard` was
    // assigned — so `takDashboard.render(...)` from the razor page threw
    // "takDashboard is undefined", silently swallowed by the empty catch
    // in OnAfterRenderAsync. The dashboard then rendered with blank chart
    // areas and no visible error. Removing the broken reference fixes it.)
    window.takDashboard = {
        render: function (data) {
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
        },
        formatAmount: formatAmount,
        formatMillions: formatMillions
    };
})();