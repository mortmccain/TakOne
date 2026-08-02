// theme.js — Tak theme manager (v3 — works with <RadzenTheme>)
//
// FOUC (flash-of-unstyled-content) prevention + runtime theme switching
// for the TakOne WebUI.
//
// HOW IT WORKS WITH <RadzenTheme>:
//   App.razor renders `<RadzenTheme Theme="standard-dark" />`. In .NET 10
//   InteractiveServer mode, <RadzenTheme> injects its <link> through
//   Blazor's HeadOutlet — meaning the link is NOT in the initial HTML
//   when this script's IIFE runs. It only appears AFTER Blazor hydrates
//   and the HeadOutlet renders.
//
//   To handle this, we use a MutationObserver on <head> that watches for
//   <link> elements being added. When a Radzen theme link appears, we
//   immediately swap its href to the saved theme (and layer the tak-light
//   override on top if needed). This gives us FOUC-safe theme application
//   even though the link is injected late by Blazor.
//
// SUPPORTED THEMES:
//   1. BUILT-IN RADZEN THEMES (standard-dark, standard, material, etc.)
//      — Single Radzen CSS file from /_content/Radzen.Blazor/css/{theme}.css
//   2. CUSTOM TAK THEMES (tak-light)
//      — Radzen BASE theme + Tak override CSS layered on top
//
// PERSISTENCE:
//   - localStorage key "radzen_theme" holds the theme name string.
//   - Default for first-time visitors: 'tak-light'

(function () {
    'use strict';

    // ---- Registry of custom Tak themes -----------------------------------
    // Each entry: "for this theme name, load <base> as the Radzen base
    // theme, then layer <override> on top."
    var TAK_THEMES = {
        'tak-light': {
            base: 'standard',                                  // Radzen light theme
            override: '/css/themes/tak-light.css?v=10'          // Tak Light palette + utilities
            // ?v=10 cache-bust: bump when tak-light.css changes
        },
        'tak-dark': {
            base: 'standard-dark',                             // Radzen dark theme
            override: '/css/themes/tak-dark.css?v=1'            // Tak Dark palette + utilities
        // ?v=1 cache-bust: bump when tak-dark.css changes
        }
    };

    var OVERRIDE_LINK_ATTR = 'data-tak-theme-override';
    var RADZEN_LINK_SELECTOR = 'link[href*="Radzen.Blazor/css/"]';

    // ---- State: the theme we want to apply --------------------------------
    // Read from localStorage ONCE at script-load time. The MutationObserver
    // and applyTheme() both reference this. We DON'T re-read from localStorage
    // on every call because the user might change themes at runtime via
    // takTheme.set() — that updates localStorage AND this variable together.
    var _desiredTheme = 'tak-light'; // default for first-time visitors

    try {
        var saved = localStorage.getItem('radzen_theme');
        if (saved) {
            _desiredTheme = saved;
        }
    } catch (e) {
        // localStorage may throw in private-browsing mode — fall back to default.
    }

    // ---- Core: find the Radzen theme <link> -------------------------------
    // Looks for any <link> in <head> whose href contains "Radzen.Blazor/css/".
    // Skips the override link (which points to /css/themes/, not Radzen).
    function findThemeLink() {
        var links = document.head.querySelectorAll(RADZEN_LINK_SELECTOR);
        for (var i = 0; i < links.length; i++) {
            if (!links[i].hasAttribute(OVERRIDE_LINK_ATTR)) {
                return links[i];
            }
        }
        return null;
    }

    // ---- Core: apply a theme by name -------------------------------------
    // 1. Resolve the Radzen base theme + (optional) override path.
    // 2. Swap the Radzen <link> href (if found).
    // 3. Remove any previous Tak override <link>.
    // 4. Append the new override <link> if the theme has one.
    function applyTheme(theme) {
        if (!theme) theme = 'standard-dark';

        var entry = TAK_THEMES[theme];
        var radzenBase = entry ? entry.base : theme;
        var overridePath = entry ? entry.override : null;

        // 1. Swap the Radzen theme link's href (if the link exists yet).
        var radzenLink = findThemeLink();
        if (radzenLink) {
            var newHref = '/_content/Radzen.Blazor/css/' + radzenBase + '.css';
            if (radzenLink.getAttribute('href') !== newHref) {
                radzenLink.setAttribute('href', newHref);
            }
        }
        // If the link doesn't exist yet (Blazor hasn't hydrated), the
        // MutationObserver will catch it when it appears and call
        // applyTheme() again.

        // 2. Remove any previously-applied Tak override link.
        var existingOverride = document.head.querySelector('link[' + OVERRIDE_LINK_ATTR + ']');
        if (existingOverride) {
            existingOverride.parentNode.removeChild(existingOverride);
        }

        // 3. Append the new override link if this theme has one.
        //    Appended AFTER the Radzen link so it wins the CSS cascade.
        if (overridePath) {
            var link = document.createElement('link');
            link.rel = 'stylesheet';
            link.href = overridePath;
            link.setAttribute(OVERRIDE_LINK_ATTR, '');
            document.head.appendChild(link);
        }

        // 4. Persist the user's choice.
        try {
            localStorage.setItem('radzen_theme', theme);
            _desiredTheme = theme;
        } catch (e) {
            // localStorage may throw — page still renders with the chosen
            // theme for this session; it just won't survive a reload.
        }
    }

    // ---- Public API ------------------------------------------------------
    // Exposed as window.takTheme so MainLayout.razor can switch themes at
    // runtime without a full page reload.
    window.takTheme = {
        set: function (theme) {
            applyTheme(theme);
        },
        get: function () {
            try {
                return localStorage.getItem('radzen_theme') || 'tak-light';
            } catch (e) {
                return 'tak-light';
            }
        },
        isCustom: function (theme) {
            return Object.prototype.hasOwnProperty.call(TAK_THEMES, theme);
        }
    };

    // ---- Initial FOUC-safe application -----------------------------------
    //
    // Two paths:
    //
    //   PATH A — the Radzen theme <link> is ALREADY in the DOM (e.g. if
    //   App.razor uses a static <link> instead of <RadzenTheme>). We swap
    //   it immediately, no waiting.
    //
    //   PATH B — the link is NOT yet in the DOM (because <RadzenTheme>
    //   injects it via HeadOutlet AFTER Blazor hydrates). We set up a
    //   MutationObserver on <head> that fires whenever child nodes are
    //   added. When the Radzen link appears, we swap it + add the override.
    //   The observer disconnects itself after the first swap to avoid
    //   needless work.
    //
    //   PATH B is the common case with <RadzenTheme>. The brief flash of
    //   standard-dark before the saved theme applies is unavoidable with
    //   <RadzenTheme> — only a static <link> in the HTML can eliminate it.

    function applyDesiredTheme() {
        applyTheme(_desiredTheme);
    }

    // Try PATH A first — if the link is already here, swap immediately.
    if (findThemeLink()) {
        // Only swap if the desired theme is NOT standard-dark (the default
        // that <RadzenTheme> renders). Saves a needless CSS refetch.
        if (_desiredTheme !== 'standard-dark') {
            applyDesiredTheme();
        }
    } else {
        // PATH B — set up a MutationObserver to catch the link when
        // <RadzenTheme> injects it after Blazor hydrates.
        if (typeof MutationObserver !== 'undefined') {
            var observer = new MutationObserver(function (mutations) {
                // Check if a Radzen theme link has appeared.
                if (findThemeLink()) {
                    observer.disconnect();
                    applyDesiredTheme();
                }
            });
            observer.observe(document.head, { childList: true, subtree: false });

            // Safety net: if the observer hasn't fired within 3 seconds
            // (e.g. Blazor failed to hydrate, or <RadzenTheme> rendered
            // before this script loaded), disconnect and try once more.
            setTimeout(function () {
                observer.disconnect();
                if (findThemeLink() && _desiredTheme !== 'standard-dark') {
                    applyDesiredTheme();
                }
            }, 3000);
        }
    }
})();
