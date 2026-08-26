// mobile.js — device detection + redirect logic for TakOne mobile pages
//
// HOW IT WORKS:
//   1. On page load, checks if the device is mobile (viewport ≤ 768px OR
//      UA matches Android/iPhone/iPad/iPod).
//   2. If mobile AND the current path is a PC-only route (e.g. /Products),
//      redirects to the mobile equivalent (/m/Products).
//   3. If desktop AND the current path is a mobile route (/m/...), redirects
//      back to the PC equivalent.
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
    // Only pages that have a mobile version are listed here.
    // v2: added Cart, Sales, Settings, and the admin pages so the whole
    //     lower-nav flow stays on mobile once a user lands there.
    var pcToMobile = {
        '/Products': '/m/Products',
        '/Cart': '/m/Cart',
        '/Sales': '/m/Sales',
        '/Settings': '/m/Settings',
        '/Admin/Products': '/m/Admin/Products',
        '/Admin/Users': '/m/Admin/Users',
        '/Admin/Categories': '/m/Admin/Categories',
        '/Admin/Groups': '/m/Admin/Groups'
    };
    var mobileToPc = {};
    Object.keys(pcToMobile).forEach(function (pc) {
        mobileToPc[pcToMobile[pc]] = pc;
    });

    function redirect() {
        if (!isMobile()) return;

        var path = window.location.pathname;
        var mobilePath = pcToMobile[path];
        if (mobilePath) {
            window.location.href = mobilePath;
            return;
        }

        // Also handle query strings (e.g. /Products?categoryId=...)
        var basePath = path.split('?')[0];
        mobilePath = pcToMobile[basePath];
        if (mobilePath) {
            var query = window.location.search || '';
            window.location.href = mobilePath + query;
            return;
        }
    }

    return {
        isMobile: isMobile,
        redirect: redirect
    };
})();

// Auto-redirect on page load
document.addEventListener('DOMContentLoaded', function () {
    if (window.takMobile) {
        window.takMobile.redirect();
    }
});