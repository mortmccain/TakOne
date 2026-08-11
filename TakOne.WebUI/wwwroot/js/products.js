/* =============================================================================
   products.js — shop page interactivity
   -----------------------------------------------------------------------------
   Currently provides:
     - Ctrl+F → focus the shop search bar (and prevent the browser's native
       find-in-page from opening). User asked to replace the "Ctrl+K" badge
       on the search bar with "Ctrl+F" and bind the actual shortcut.
     - A small helper to focus the search bar programmatically (e.g. from
       a Blazor onClick handler).

   Loaded via App.razor's <head> so it's available on every page. The
   keydown listener only acts when the shop search input (#tm-shop-search-input)
   is present in the DOM (i.e. we're on the /Products shop page).

   BLAZOR ENHANCED NAVIGATION:
     In .NET 10 Blazor SSR with enhanced navigation, the <head> scripts load
     once on the initial page request and persist across navigations. The
     document-level keydown listener registered here therefore survives
     enhanced navigation. We also listen for Blazor's `enhanced-load` event
     (fired after every enhanced navigation completes) as a belt-and-suspenders
     re-registration trigger — in case a future Blazor update changes the
     listener-persistence semantics.
   ========================================================================== */

(function () {
    'use strict';

    // The shop search input has id="tm-shop-search-input" (set in Products.razor).
    // We resolve it lazily inside the handler so we don't care about load order
    // or which page is currently rendered.
    function focusShopSearch() {
        var el = document.getElementById('tm-shop-search-input');
        if (el) {
            el.focus();
            // Move caret to end so the user can keep typing from where they were
            var len = (el.value || '').length;
            try {
                el.setSelectionRange(len, len);
            } catch (e) {
                // Some input types don't support setSelectionRange — ignore.
            }
        }
    }

    // Global keydown listener for Ctrl+F (or Cmd+F on macOS).
    // preventDefault() stops the browser's native find bar from opening —
    // user explicitly asked for this behavior on the shop page.
    //
    // The listener is registered globally but only ACTS when the shop
    // search input is present in the DOM (i.e. we're on the shop page).
    // On other pages, the check `if (!el) return;` lets the browser's
    // native Ctrl+F find-in-page work normally.
    function onKeyDown(e) {
        var isFindShortcut = (e.ctrlKey || e.metaKey) && (e.key === 'f' || e.key === 'F');
        if (!isFindShortcut) return;

        // Only intercept when the shop search input is present (shop page only).
        var el = document.getElementById('tm-shop-search-input');
        if (!el) return;

        e.preventDefault();
        e.stopPropagation();
        focusShopSearch();
    }

    // Register the listener. Idempotent — safe to call multiple times
    // (the { passive: false } option lets us call preventDefault()).
    function register() {
        // Guard against double-registration on Blazor enhanced-load.
        if (window.__tmShopSearchKeydownRegistered) return;
        window.__tmShopSearchKeydownRegistered = true;
        document.addEventListener('keydown', onKeyDown, { passive: false });
    }

    // Register immediately (covers the initial full page load).
    register();

    // Re-register after Blazor enhanced navigation completes, in case a
    // future Blazor update changes listener-persistence semantics. The
    // idempotent guard inside register() prevents double-listening.
    document.addEventListener('enhanced-load', register);

    // Public API — callable from Blazor via JSRuntime.InvokeVoidAsync.
    // Not currently used (Ctrl+F is enough), but kept for future use
    // (e.g. a "search" button elsewhere on the page that focuses the bar).
    window.tmShopPage = {
        focusSearch: focusShopSearch
    };
})();