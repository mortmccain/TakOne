// theme.js — Tak theme manager
//
// FOUC (flash-of-unstyled-content) prevention + runtime theme switching
// for the TakOne WebUI.
//
// SUPPORTS TWO KINDS OF THEMES:
//
//   1. BUILT-IN RADZEN THEMES (e.g. "standard-dark", "material", "dark")
//      — A single Radzen CSS file is loaded from
//        /_content/Radzen.Blazor/css/{theme}.css. No override layer.
//
//   2. CUSTOM TAK THEMES (e.g. "tak-light", future "tak-dark")
//      — A Radzen light/dark BASE theme is loaded first, then a Tak
//        override CSS file at /css/themes/{theme}.css is layered on top.
//      — The override file re-points Radzen's CSS variables to Tak's
//        brand palette + adds Tak-specific utility classes.
//
// LOADING ORDER (for Tak Light):
//   1. /_content/Radzen.Blazor/Radzen.Blazor.css           (base component CSS — always loaded by App.razor)
//   2. /_content/Radzen.Blazor/css/standard.css            (Radzen light theme — BASE for Tak Light)
//   3. /css/themes/tak-light.css                           (Tak Light override)
//
// ARCHITECTURE:
//   - App.razor renders a STATIC <link id="tak-radzen-theme"> with the
//     default href "_content/Radzen.Blazor/css/standard-dark.css".
//   - WHY STATIC, NOT <RadzenTheme>: in .NET 10 InteractiveServer mode,
//     <RadzenTheme> renders its <link> through the HeadOutlet, which means
//     the link is NOT in the initial prerendered HTML — it's only injected
//     AFTER Blazor hydrates. This script runs synchronously in <head>
//     BEFORE Blazor hydrates, so it would find NO link to swap. By using
//     a static <link> with a stable ID, the link is GUARANTEED to be in
//     the DOM when this script runs.
//   - This script finds #tak-radzen-theme by ID and swaps its href to
//     the saved theme (and appends a Tak override link if the saved
//     theme is a custom Tak theme).
//   - MainLayout.razor's theme dropdown calls window.takTheme.set(theme)
//     at runtime to switch themes without a full page reload.
//
// PERSISTENCE:
//   - localStorage key "radzen_theme" holds the theme name string.
//   - Set by window.takTheme.set().
//   - Read here on every page load (FOUC-safe).
//   - If the key is ABSENT (first-time visitor), we default to 'tak-light'
//     so the Tak brand look is what new users see immediately. This
//     matches MainLayout.razor's _selectedTheme default.
//
// CUSTOM THEME REGISTRY:
//   To add a new Tak theme (e.g. "tak-dark"), add an entry to
//   TAK_THEMES below. The key is the theme name as stored in localStorage;
//   the value is { base: <radzen-theme>, override: <css-path> }.
//   Also add the theme to the dropdown list in MainLayout.razor.

(function () {
    'use strict';

    // ---- Registry of custom Tak themes -----------------------------------
    //
    // Each entry says: "for this theme name, load <base> as the Radzen
    // base theme, then layer <override> on top."
    //
    // Themes NOT in this map are assumed to be plain Radzen themes — we
    // load /_content/Radzen.Blazor/css/{theme}.css and remove any
    // previously-added override link.
    var TAK_THEMES = {
        'tak-light': {
            base: 'standard',                                  // Radzen light theme
            override: '/css/themes/tak-light.css?v=3'           // Tak Light palette + utilities
            // ?v=3 cache-bust: bump when tak-light.css changes
        }
        // Future: 'tak-dark' will go here, with base='dark' or 'standard-dark'.
    };

    var OVERRIDE_LINK_ATTR = 'data-tak-theme-override';
    var THEME_LINK_ID = 'tak-radzen-theme';

    // ---- Core: find the Radzen theme <link> -------------------------------
    //
    // PRIMARY LOOKUP: by the stable ID #tak-radzen-theme (set in App.razor).
    // This is 100% reliable — the ID is on a static <link> element that's
    // always in the initial HTML.
    //
    // FALLBACK: if the ID lookup fails (shouldn't happen, but defensive),
    // fall back to a selector that matches any <link> whose href contains
    // "Radzen.Blazor/css/". This catches the case where App.razor might
    // revert to <RadzenTheme> in the future, or where a different page
    // renders the theme link without our ID.
    function findThemeLink() {
        var link = document.getElementById(THEME_LINK_ID);
        if (link) return link;

        // Fallback: selector-based lookup. Matches links like:
        //   <link href="_content/Radzen.Blazor/css/standard-dark.css">
        //   <link href="/_content/Radzen.Blazor/css/standard.css">
        var allLinks = document.head.querySelectorAll('link[href*="Radzen.Blazor/css/"]');
        for (var i = 0; i < allLinks.length; i++) {
            // Skip the override link if it happens to match (it shouldn't
            // since override paths point to /css/themes/, not Radzen.Blazor/css/).
            if (!allLinks[i].hasAttribute(OVERRIDE_LINK_ATTR)) {
                return allLinks[i];
            }
        }
        return null;
    }

    // ---- Core: apply a theme by name -------------------------------------
    //
    // 1. Resolve the Radzen base theme + (optional) override path.
    // 2. Swap the existing Radzen <link> href.
    // 3. Remove any previous Tak override <link>.
    // 4. If the new theme has an override, append it.
    //
    // This is safe to call multiple times — it's idempotent.
    function applyTheme(theme) {
        // Default to standard-dark if no theme is provided (matches the
        // static <link> default href in App.razor).
        if (!theme) theme = 'standard-dark';

        var entry = TAK_THEMES[theme];
        var radzenBase = entry ? entry.base : theme;
        var overridePath = entry ? entry.override : null;

        // 1. Swap the Radzen theme link's href.
        var radzenLink = findThemeLink();
        if (radzenLink) {
            var newHref = '/_content/Radzen.Blazor/css/' + radzenBase + '.css';
            // Only write the href if it's actually different — avoids
            // pointlessly triggering a CSS refetch when re-applying the
            // same theme (e.g. user clicks the already-active theme).
            if (radzenLink.getAttribute('href') !== newHref) {
                radzenLink.setAttribute('href', newHref);
            }
        } else {
            // This should not happen — the static <link id="tak-radzen-theme">
            // is always in the HTML. If it does, log a warning so the
            // developer knows the theme link is missing.
            console.warn('[takTheme] No Radzen theme <link> found in <head>. ' +
                'Expected <link id="' + THEME_LINK_ID + '">. ' +
                'Theme "' + theme + '" was NOT applied.');
        }

        // 2. Remove any previously-applied Tak override link.
        //    We use a data-attribute marker so we don't accidentally
        //    remove a different stylesheet that happened to be added
        //    by another script.
        var existingOverride = document.head.querySelector('link[' + OVERRIDE_LINK_ATTR + ']');
        if (existingOverride) {
            existingOverride.parentNode.removeChild(existingOverride);
        }

        // 3. Append the new override link if this theme has one.
        if (overridePath) {
            var link = document.createElement('link');
            link.rel = 'stylesheet';
            link.href = overridePath;
            link.setAttribute(OVERRIDE_LINK_ATTR, '');
            // Append AFTER the Radzen link so the override wins the cascade.
            // CSS specificity rules: our override file uses the same
            // selectors as Radzen's theme so later-in-DOM wins.
            document.head.appendChild(link);
        }

        // 4. Persist the user's choice.
        try {
            localStorage.setItem('radzen_theme', theme);
        } catch (e) {
            // localStorage may throw in private-browsing mode or if
            // cookies are blocked. Silently fall back to not persisting
            // — the page still renders with the chosen theme for this
            // session; it just won't survive a reload.
        }
    }

    // ---- Public API ------------------------------------------------------
    //
    // Exposed as window.takTheme so MainLayout.razor (and any future
    // code) can switch themes at runtime without a full page reload.
    //
    //   takTheme.set('tak-light')        → apply Tak Light + persist
    //   takTheme.set('standard-dark')    → apply Radzen standard-dark + persist
    //   takTheme.get()                   → returns the currently-saved theme
    //                                        (or 'tak-light' if none saved)
    window.takTheme = {
        set: function (theme) {
            applyTheme(theme);
        },
        get: function () {
            try {
                // Default to 'tak-light' (NOT 'standard-dark') so the
                // dropdown in MainLayout.razor matches what the IIFE below
                // actually applied on initial load. MainLayout.razor's
                // _selectedTheme field is also initialized to 'tak-light',
                // so first-time users see a consistent state: dropdown says
                // "Tak Light", the page IS Tak Light, and localStorage gets
                // seeded with 'tak-light' on the first manual switch.
                return localStorage.getItem('radzen_theme') || 'tak-light';
            } catch (e) {
                return 'tak-light';
            }
        },
        // Returns true if the given theme name is a custom Tak theme
        // (i.e. has an override CSS file). Used by MainLayout.razor to
        // know whether to expect a layered override or a plain Radzen
        // theme. Currently unused but exposed for future UI (e.g.
        // showing a "Tak" badge next to custom themes in the dropdown).
        isCustom: function (theme) {
            return Object.prototype.hasOwnProperty.call(TAK_THEMES, theme);
        }
    };

    // ---- Initial FOUC-safe application -----------------------------------
    //
    // Runs synchronously when this script loads (in <head>, before the
    // browser paints <body>). The static <link id="tak-radzen-theme"> in
    // App.razor is already in the DOM at this point (it appears before
    // this <script> tag in <head>), so findThemeLink() will find it.
    //
    // DEFAULT THEME: if nothing is saved in localStorage, we apply
    // 'tak-light' (the project's primary custom theme) so first-time
    // visitors immediately see the Tak brand look, not Radzen's default
    // dark theme. This matches MainLayout.razor's _selectedTheme = "tak-light"
    // default — without this, the dropdown would say "Tak Light" while
    // the page actually rendered as standard-dark (a confusing mismatch).
    try {
        var saved = localStorage.getItem('radzen_theme') || 'tak-light';
        // Skip the swap ONLY if the saved theme is already what the
        // static <link> in App.razor emits (standard-dark). For any other
        // theme — including the 'tak-light' default — we have to swap.
        if (saved !== 'standard-dark') {
            applyTheme(saved);
        }
    } catch (e) {
        // localStorage may throw in private-browsing mode or if cookies
        // are blocked. Silently fall back to the default theme — the
        // page still renders, just with standard-dark instead of the
        // saved preference.
        console.warn('[takTheme] Could not read localStorage; falling back to default theme.', e);
    }
})();
