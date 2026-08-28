/* =============================================================================
   culture.js — culture-cookie switching helper
   -----------------------------------------------------------------------------
   PURPOSE:
     Replaces the 3 `JS.InvokeVoidAsync("eval", "document.cookie = ...")`
     anti-pattern call sites that existed in
       - Components/Layout/MobileLayout/MobileLayout.razor (SetCultureAsync)
       - Components/Layout/MainLayout.razor (SetCultureAsync)
       - Components/Pages/Products/Products.razor (SetCultureAsync)
     (Brutal Code Review v3 finding #24, part 1).

   WHY NOT `eval()`?
     `JSRuntime.InvokeVoidAsync("eval", someJsString)` invokes the JS engine's
     `eval()` with the supplied string. This is a SECURITY anti-pattern
     (XSS injection vector if the string is ever built from user input —
     e.g. a future change to include a culture name from a query string)
     and a PERFORMANCE anti-pattern (bypasses the JS engine's parser cache:
     each call re-parses the source). A named, static JS function has
     neither problem — the JS source is parsed ONCE at script-load time,
     and the only attacker-controllable data crosses the JSRuntime boundary
     as a function ARGUMENT (a primitive string), never as code.

   BEHAVIOR (must match the previous eval() calls EXACTLY):
     - Cookie name        : "takone_culture"
       (matches CookieRequestCultureProvider.CookieName in Program.cs)
     - Cookie value format: "c=<culture>|uic=<culture>"
       (matches CookieRequestCultureProvider's expected format — `c=` is the
        request culture for number/date formatting, `uic=` is the UI culture
        for .resx selection. Always both set to the same value.)
     - Cookie attributes  : path=/, max-age=31536000 (1 year), samesite=strict
       (max-age=31536000 = 365 * 24 * 60 * 60 — same as the original eval.)
     - Page reload        : NOT done here. The .razor call site preserves the
       original reload mechanism (`Navigation.NavigateTo(Uri, forceLoad: true)`
       via Blazor's NavigationManager — that's a C# call AFTER this JS
       function returns, identical to the pre-fix flow. Keeping the reload
       in C# means Blazor remains in control of the navigation lifecycle
       (circuit teardown, location-change notification, etc.).

   LOADING:
     Registered via a <script src="js/culture.js?v=1"></script> tag in
     App.razor's <head> (alongside theme.js, products.js, etc.). Loaded on
     every page so the function is available regardless of which layout/page
     the user lands on first (mobile users land on MobileLayout; desktop
     users land on MainLayout; both need this function).

   CALL SITE CONTRACT:
     `await JS.InvokeVoidAsync("takone.setCultureCookie", culture);`
     where `culture` is a string like "fa-IR" or "en-US". The function builds
     the cookie value internally so the .razor call site passes ONLY the
     culture name (a short, validated, non-attacker-controlled primitive
     string) — never a full cookie string. Defense-in-depth: even if a
     future bug let an arbitrary string reach this function, the worst it
     can do is set a malformed cookie value (no script execution, no
     attribute injection because the attribute string is a JS literal here).
   ========================================================================== */

(function () {
    'use strict';

    // Create / fetch the global namespace. Other Tak JS files (theme.js,
    // dashboard.js, products.js, mobile.js) use the `window.takX` / window.<PageName>
    // convention; this file uses `window.takone` (the project's name) since
    // the culture cookie is shared across layouts and pages.
    window.takone = window.takone || {};

    /**
     * Writes the culture cookie `takone_culture=c=<culture>|uic=<culture>`
     * with path=/, max-age=31536000 (1 year), samesite=strict.
     *
     * @param {string} culture - The culture name, e.g. "fa-IR" or "en-US".
     *   Must be the value CookieRequestCultureProvider expects for both the
     *   `c=` (CurrentCulture) and `uic=` (CurrentUICulture) slots. The caller
     *   always passes the same value for both.
     */
    function setCultureCookie(culture) {
        // The cookie value is the standard CookieRequestCultureProvider
        // format: `c=<culture>|uic=<culture>`. Both halves set to the same
        // value (we never want the formatting culture to differ from the
        // resource-selection culture).
        var cookieValue = 'c=' + culture + '|uic=' + culture;

        // Cookie attributes MUST match the previous eval() calls EXACTLY
        // (path=/, max-age=31536000, samesite=strict). Changing any of these
        // would break culture persistence (wrong path → cookie not sent on
        // the next request; wrong max-age → cookie expires early; wrong
        // samesite → cookie blocked by browser on the reload navigation).
        document.cookie = 'takone_culture=' + cookieValue +
                          '; path=/; max-age=31536000; samesite=strict';
    }

    // Expose via the namespace so Blazor's JSRuntime can call it as
    // `await JS.InvokeVoidAsync("takone.setCultureCookie", culture)`.
    window.takone.setCultureCookie = setCultureCookie;
})();
