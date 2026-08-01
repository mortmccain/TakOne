/* =============================================================================
   products.js — shop page interactivity
   -----------------------------------------------------------------------------
   Currently provides:
     - Ctrl+F → focus the shop search bar (and prevent the browser's native
       find-in-page from opening). User asked to replace the "Ctrl+K" badge
       on the search bar with "Ctrl+F" and bind the actual shortcut.
     - A small helper to focus the search bar programmatically (e.g. from
       a Blazor onClick handler).

   Loaded only when needed; the script element is added by App.razor so it's
   available on every page (cheap — the listener only fires on Ctrl+F).
   ========================================================================== */

(
    function ()
    {
    'use strict';

    // The shop search input has id="tm-shop-search-input" (set in Products.razor).
    // We resolve it lazily inside the handler so we don't care about load order.
        function focusShopSearch()
        {
        var el = document.getElementById('tm-shop-search-input');
            if (el)
            {
            el.focus();
            // Move caret to end so the user can keep typing from where they were
            var len = (el.value || '').length;
                try
                {
                el.setSelectionRange(len, len);
                } catch (e)
                {
                // Some input types don't support setSelectionRange — ignore.
                }
            }
        }

    // Global keydown listener for Ctrl+F (or Cmd+F on macOS).
    // preventDefault() stops the browser's native find bar from opening —
    // user explicitly asked for this behavior on the shop page.
    //
    // We DO NOT preventDefault on Cmd+F for non-shop pages, but the listener
    // is registered globally. The check is: only intercept when the shop
    // search input exists in the DOM (i.e. we're on the shop page).
        document.addEventListener
            (
                'keydown', function (e)
        {
        var isFindShortcut =
            (e.ctrlKey || e.metaKey) && (e.key === 'f' || e.key === 'F');

        if (!isFindShortcut) return;

        // Only intercept when the shop search input is present (shop page only).
        var el = document.getElementById('tm-shop-search-input');
        if (!el) return;

        e.preventDefault();
        focusShopSearch();
        },
                { passive: false }
            );

    // Public API — callable from Blazor via JSRuntime.InvokeVoidAsync.
    // Not currently used (Ctrl+F is enough), but kept for future use
    // (e.g. a "search" button elsewhere on the page that focuses the bar).
        window.tmShopPage =
        {
        focusSearch: focusShopSearch
        };
    }
)
    ();