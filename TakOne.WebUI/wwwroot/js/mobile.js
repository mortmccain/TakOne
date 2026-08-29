// mobile.js — device detection + redirect logic + pull-to-refresh for TakOne mobile pages
//
// HOW IT WORKS:
//   1. On page load, checks if the device is mobile (viewport ≤ 768px OR
//      UA matches Android/iPhone/iPad/iPod).
//   2. If mobile AND the current path is a PC-only route (e.g. /Products),
//      redirects to the mobile equivalent (/m/Products).
//   3. If desktop AND the current path is a mobile route (/m/...), redirects
//      back to the PC equivalent.
//   4. Pull-to-refresh: on touch devices, when the user is at the top of
//      the scroll container and pulls down past the threshold, the page
//      reloads to refresh server-side data (mimics native app feel).
//
// The redirect is a full-page navigation (forceLoad) so the server re-
// evaluates the route from scratch. This avoids Blazor circuit issues.

window.takMobile = (function () {
    function isMobile() {
        var width = window.innerWidth || document.documentElement.clientWidth;
        var ua = navigator.userAgent || '';
        var isMobileUA = /Android|iPhone|iPad|iPod/i.test(ua);
        var isSmallScreen = width <= 768;
        return isMobileUA || isSmallScreen;
    }

    // Map of PC routes → mobile routes.
    // v2: added Cart, Sales, Settings, and the admin pages so the whole
    //     lower-nav flow stays on mobile once a user lands there.
    // v3 (Round 7): added detail routes — /Sales/{id}, /Products/{id},
    //     /Admin/Users/{id}, /Admin/Groups/Edit/{id}, and the /m/Dashboard
    //     home route so staff tapping Dashboard stay in the mobile shell.
    var pcToMobile = {
        '/Products': '/m/Products',
        '/Cart': '/m/Cart',
        '/Sales': '/m/Sales',
        '/Settings': '/m/Settings',
        '/Admin/Products': '/m/Admin/Products',
        '/Admin/Users': '/m/Admin/Users',
        '/Admin/Categories': '/m/Admin/Categories',
        '/Admin/Groups': '/m/Admin/Groups',
        '/Dashboard': '/m/Dashboard',
        // Round 8: redirect the desktop Create pages to mobile equivalents so
        // admins using the mobile list pages' "Create" buttons stay on mobile.
        '/Admin/Products/Create': '/m/Admin/Products/Create',
        '/Admin/Users/Create': '/m/Admin/Users/Create',
        '/Admin/Groups/Create': '/m/Admin/Groups/Create'
    };
    var mobileToPc = {};
    Object.keys(pcToMobile).forEach(function (pc) {
        mobileToPc[pcToMobile[pc]] = pc;
    });

    // Detail-route prefix redirects (PC → mobile).
    // When the path starts with one of these prefixes AND continues with
    // a /{id} segment, we redirect to the mobile equivalent preserving
    // the rest of the path + query string.
    // e.g. /Sales/abc-123 → /m/Sales/abc-123
    //      /Admin/Groups/Edit/abc-123 → /m/Admin/Groups/Edit/abc-123
    var pcToMobilePrefixes = [
        { pc: '/Sales/',                       mobile: '/m/Sales/' },
        { pc: '/Products/',                    mobile: '/m/Products/' },
        { pc: '/Admin/Users/',                 mobile: '/m/Admin/Users/' },
        { pc: '/Admin/Groups/Edit/',           mobile: '/m/Admin/Groups/Edit/' }
    ];

    function redirect() {
        var path = window.location.pathname;

        if (isMobile()) {
            // Exact match (e.g. /Products → /m/Products)
            var mobilePath = pcToMobile[path];
            if (mobilePath) {
                window.location.href = mobilePath;
                return;
            }

            // Prefix match (e.g. /Sales/abc-123 → /m/Sales/abc-123)
            for (var i = 0; i < pcToMobilePrefixes.length; i++) {
                var entry = pcToMobilePrefixes[i];
                if (path.indexOf(entry.pc) === 0) {
                    var rest = path.substring(entry.pc.length - 1); // keep leading slash
                    window.location.href = entry.mobile.substring(0, entry.mobile.length - 1) + rest + (window.location.search || '');
                    return;
                }
            }

            // Query-string variants of the exact match (rare but possible)
            var basePath = path.split('?')[0];
            mobilePath = pcToMobile[basePath];
            if (mobilePath) {
                var query = window.location.search || '';
                window.location.href = mobilePath + query;
                return;
            }
            return;
        }

        // DESKTOP reverse redirect: a desktop user who lands on a mobile
        // route (bookmark, shared link, window resized wide) is sent back
        // to the PC equivalent — the behavior documented in the header
        // comment but previously not implemented (the mobileToPc map was
        // computed and then discarded). This uses the UA-based mobile
        // check only for the reverse direction so a desktop user with a
        // temporarily narrow window isn't ping-ponged between shells.
        var isMobileUA = /Android|iPhone|iPad|iPod/i.test(navigator.userAgent || '');
        if (isMobileUA) return;

        var pcPath = mobileToPc[path];
        if (pcPath) {
            window.location.href = pcPath + (window.location.search || '');
            return;
        }

        // Prefix match for mobile detail routes (e.g. /m/Sales/abc-123 →
        // /Sales/abc-123). Built from the same prefix table so the two
        // directions can never drift apart.
        for (var j = 0; j < pcToMobilePrefixes.length; j++) {
            var e = pcToMobilePrefixes[j];
            if (path.indexOf(e.mobile) === 0) {
                var tail = path.substring(e.mobile.length - 1); // keep leading slash
                window.location.href = e.pc.substring(0, e.pc.length - 1) + tail + (window.location.search || '');
                return;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Pull-to-refresh
    //
    // We attach a touchstart/touchmove/touchend listener to the document
    // body. When the user starts a touch at scrollTop == 0 (top of the
    // page) and drags down past a 70px threshold, we:
    //   1. Insert a fixed-position .m-ptr-indicator element at the top
    //      of the viewport (CSS in mobile.css handles the slide-down).
    //   2. On release past the threshold, we set the indicator's
    //      `refreshing` class (CSS shows a spinning chevron) and call
    //      window.location.reload() to refresh server-side data.
    //   3. If the user releases before the threshold, we hide the
    //      indicator and no reload happens.
    //
    // We DON'T intercept the touch when:
    //   - The user is NOT at the top of the scroll (scrollTop > 0)
    //   - The active element is an input/textarea/select (so the user
    //     can scroll within a form field without triggering PTR)
    //   - The page is a desktop (isMobile() returns false)
    //
    // We DON'T use passive listeners because we need to call
    // preventDefault() on touchmove to stop the native rubber-band
    // effect while the indicator is visible.
    // ─────────────────────────────────────────────────────────────

    var PTR_THRESHOLD = 70;           // px the user must pull down
    var PTR_INDICATOR_ID = 'm-ptr-indicator';
    var ptrIndicator = null;
    var ptrPulling = false;
    var ptrStartY = 0;
    var ptrCurrentDelta = 0;

    function ensurePtrIndicator() {
        if (ptrIndicator) return ptrIndicator;
        ptrIndicator = document.getElementById(PTR_INDICATOR_ID);
        if (!ptrIndicator) {
            ptrIndicator = document.createElement('div');
            ptrIndicator.id = PTR_INDICATOR_ID;
            ptrIndicator.className = 'm-ptr-indicator';
            ptrIndicator.innerHTML =
                '<div class="m-ptr-pill">' +
                '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5">' +
                '<path stroke-linecap="round" stroke-linejoin="round" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />' +
                '</svg>' +
                '<span class="m-ptr-label">Release to refresh</span>' +
                '</div>';
            document.body.appendChild(ptrIndicator);
        }
        return ptrIndicator;
    }

    function isInteractiveTarget(el) {
        if (!el) return false;
        var tag = (el.tagName || '').toLowerCase();
        return tag === 'input' || tag === 'textarea' || tag === 'select' || el.isContentEditable;
    }

    function atScrollTop() {
        // window.scrollY for modern browsers, fallback for older iOS
        var y = window.scrollY || window.pageYOffset || document.documentElement.scrollTop || document.body.scrollTop || 0;
        return y <= 0;
    }

    function onTouchStart(e) {
        if (!isMobile()) return;
        if (!atScrollTop()) return;
        if (e.touches.length !== 1) return;
        if (isInteractiveTarget(document.activeElement)) return;

        ptrStartY = e.touches[0].clientY;
        ptrPulling = true;
        ptrCurrentDelta = 0;
    }

    function onTouchMove(e) {
        if (!ptrPulling) return;

        var y = e.touches[0].clientY;
        var delta = y - ptrStartY;
        if (delta <= 0) {
            // User pulled up — reset state and hide indicator
            ptrCurrentDelta = 0;
            var ind = ensurePtrIndicator();
            ind.classList.remove('active', 'refreshing');
            return;
        }

        // Only intercept the touch once we're sure the user is pulling
        // down past a small deadzone (10px) so we don't steal vertical
        // scrolls meant for the page itself.
        if (delta > 10) {
            e.preventDefault();
            ptrCurrentDelta = delta;
            var indicator = ensurePtrIndicator();
            // Threshold visible range = 0..PTR_THRESHOLD px (cap at PTR_THRESHOLD so it doesn't grow forever)
            var visible = Math.min(delta, PTR_THRESHOLD);
            indicator.style.height = (visible * 0.6) + 'px';
            indicator.classList.add('active');
            indicator.classList.remove('refreshing');
            var label = indicator.querySelector('.m-ptr-label');
            if (label) {
                label.textContent = delta >= PTR_THRESHOLD ? 'Release to refresh' : 'Pull down to refresh';
            }
        }
    }

    function onTouchEnd(e) {
        if (!ptrPulling) return;
        ptrPulling = false;

        var indicator = ensurePtrIndicator();
        if (ptrCurrentDelta >= PTR_THRESHOLD) {
            // User crossed the threshold — refresh
            indicator.classList.add('refreshing');
            var label = indicator.querySelector('.m-ptr-label');
            if (label) label.textContent = 'Refreshing…';
            // Slight delay so the user sees the refreshing state before the reload
            setTimeout(function () { window.location.reload(); }, 250);
        } else {
            // User released early — collapse the indicator
            indicator.classList.remove('active', 'refreshing');
            indicator.style.height = '';
        }
    }

    function bindPullToRefresh() {
        if (!isMobile()) return;
        // passive:false so we can call preventDefault on touchmove
        document.addEventListener('touchstart', onTouchStart, { passive: true });
        document.addEventListener('touchmove', onTouchMove, { passive: false });
        document.addEventListener('touchend', onTouchEnd, { passive: true });
        document.addEventListener('touchcancel', onTouchEnd, { passive: true });
    }

    return {
        isMobile: isMobile,
        redirect: redirect,
        bindPullToRefresh: bindPullToRefresh
    };
})();

// Auto-redirect + bind pull-to-refresh on page load
document.addEventListener('DOMContentLoaded', function () {
    if (window.takMobile) {
        window.takMobile.redirect();
        window.takMobile.bindPullToRefresh();
    }
});
