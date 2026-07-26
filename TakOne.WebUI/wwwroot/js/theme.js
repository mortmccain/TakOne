// theme.js — Phase 0.5
// FOUC (flash-of-unstyled-content) prevention for the Radzen theme switcher.
//
// Runs synchronously in <head> BEFORE the browser paints. The RadzenTheme
// component has already injected the default standard-dark <link> into the
// DOM by the time this script runs (because <RadzenTheme> is rendered
// before <script src="js/theme.js">). We querySelector that link and swap
// its href to the user's saved theme (if any), so the page renders with the
// correct theme on the very first paint — no flash of standard-dark for
// users who picked a different theme last time.
//
// The list of free Radzen themes (matches MainLayout.razor's switcher):
//   standard-dark (default), standard, material-dark, material,
//   software-dark, software, humanistic-dark, humanistic,
//   dark, default
//
// Persistence:
//   - localStorage key "radzen_theme" holds the theme name string.
//   - Set by MainLayout.razor's theme switcher's Change event.
//   - Read here on every page load.
(
    function () {
        try {
            var saved = localStorage.getItem('radzen_theme');
            if (!saved || saved === 'standard-dark') return;

            var link = document.querySelector('head link[href*="Radzen.Blazor/css/"]');
            if (!link) return;

            // The default href looks like:
            //   /_content/Radzen.Blazor/Radzen.Blazor.css
            // or:
            //   /_content/Radzen.Blazor/css/standard-dark.css
            // We want to end up at:
            //   /_content/Radzen.Blazor/css/{saved}.css
            var match = link.href.match(/\/_content\/Radzen\.Blazor\/(css\/[^/]+\.css)?/);

            if (!match) return;

            link.href = '/_content/Radzen.Blazor/css/' + saved + '.css';
        }

        catch (e) {
            // localStorage may throw in private-browsing mode or if cookies are
            // blocked. Silently fall back to the default theme — the page still
            // renders, just with standard-dark instead of the saved preference.

        }
    }
)
    ();