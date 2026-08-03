/* =============================================================================
   adminCategoriesTree.js — AdminCategories.razor SVG connector measurement
   -----------------------------------------------------------------------------
   The admin Categories page renders a horizontal node-tree (parent on the
   inline-start side, children stacked vertically on the inline-end side).
   Connectors between parent and child are smooth cubic-bezier curves drawn
   in an SVG overlay.

   Because node positions depend on the actual rendered DOM (font size,
   name length, expand/collapse state, viewport width), the SVG paths must
   be computed at runtime by measuring the DOM. Blazor can't do this
   directly, so we expose two functions on `window.adminCategoriesTree`:

     - measureConnectors(canvasEl)
         Walks the DOM, finds every [data-node-id] / [data-parent-id]
         pair, measures the OVAL (.node-oval inside the [data-node-id]
         wrapper) of each, and returns an array of { parentId, childId,
         path } objects where `path` is an SVG "d" attribute string for
         a cubic bezier.

         IMPORTANT: We measure .node-oval, NOT the outer [data-node-id]
         wrapper. The wrapper contains the entire subtree (children
         container + grandchildren), so its bounding rect's right edge
         is way past the oval's right edge — measuring the wrapper
         produced "disconnected" connector lines that floated in the
         middle of the canvas. Measuring the oval itself makes the
         connectors attach to the visible node shape.

     - bindRedraw(canvasEl, dotNetRef)
         Wires up a ResizeObserver on the canvas so connectors are
         re-measured whenever the canvas resizes (e.g. window resize,
         sidebar collapse, font load). Also re-measures on language
         change (the `dir` attribute flips between ltr/rtl, which
         changes which edge of each node the connector should start
         from). Returns a disconnect function.

   RTL handling:
     The page renders with <html dir="rtl"> for Persian. We detect this
     via `getComputedStyle(canvasEl).direction`. In RTL, connectors go
     from the parent's LEFT edge to the child's RIGHT edge (mirror of
     the LTR layout).

   BEZIER ALGORITHM (v7 — D3 curveBezierX):
     The cubic Bezier uses control points at the OPPOSITE endpoint's X:
       cp1 = (endX, startY)   — shares Y with start (horizontal exit)
       cp2 = (startX, endY)   — shares Y with end (horizontal entry)
     This is the exact algorithm from D3.js's curveBezierX (the curve
     behind d3.linkHorizontal). It is direction-agnostic — the same
     formula produces a correct S-curve in both LTR and RTL, and it
     NEVER degenerates to a straight line regardless of gap size.

     The previous v5/v6 formula put BOTH control points at the X
     midpoint (cp1X = cp2X = midX). That degenerated to a near-straight
     diagonal in RTL when the measured gap was small, producing the
     "lines are straight in Persian" bug. The D3 formula fixes this
     because the control points are always at the full opposite X
     coordinates, giving the curve strong horizontal "pull" at both
     endpoints.
   ========================================================================== */

(function () {
    'use strict';

    // v6: Registry of all active disconnect functions. bindRedraw pushes
    // its disconnect closure here, and on subsequent calls it drains the
    // array first — disconnecting all previous observers before creating
    // new ones. This prevents MutationObserver leaks on document.documentElement
    // when the user switches between Tree and List views repeatedly.
    var _disconnectFunctions = [];

    function isRtl(canvasEl) {
        // getComputedStyle respects inheritance — even if `direction` is
        // set on <html>, the canvas element's computed style will report
        // the inherited value. This is more reliable than checking
        // canvasEl.dir directly (which only returns a value if the
        // attribute is set ON the element, not inherited).
        return getComputedStyle(canvasEl).direction === 'rtl';
    }

    // Find the .node-oval element inside a [data-node-id] wrapper.
    // Falls back to the wrapper itself if no .node-oval is found (e.g.
    // during editing when the oval is replaced by an inline form — in
    // that case we still want a sensible rect so connectors don't
    // disappear mid-edit).
    function getOvalEl(wrapperEl) {
        if (!wrapperEl) return null;
        var oval = wrapperEl.querySelector(':scope > .node-content > .node-oval');
        if (oval) return oval;
        // Editing state — fall back to the editing oval container.
        oval = wrapperEl.querySelector(':scope > .node-content > .node-oval.is-editing');
        if (oval) return oval;
        // Last resort: the node-content block (oval + actions column).
        return wrapperEl.querySelector(':scope > .node-content') || wrapperEl;
    }

    function measureConnectors(canvasEl) {
        if (!canvasEl) return [];

        var canvasRect = canvasEl.getBoundingClientRect();
        var rtl = isRtl(canvasEl);
        var connectors = [];

        // Every node has data-node-id (its own id) and data-parent-id
        // (its parent's id, or absent for root nodes). We iterate over
        // all child nodes (those WITH data-parent-id) and look up their
        // parent by data-node-id.
        var childEls = canvasEl.querySelectorAll('[data-parent-id]');
        for (var i = 0; i < childEls.length; i++) {
            var childWrapper = childEls[i];
            var parentId = childWrapper.getAttribute('data-parent-id');
            if (!parentId) continue;

            var parentWrapper = canvasEl.querySelector(
                '[data-node-id="' + cssEscape(parentId) + '"]');
            if (!parentWrapper) continue;

            // Measure the OVALS, not the wrappers — see getOvalEl docs.
            var parentOval = getOvalEl(parentWrapper);
            var childOval = getOvalEl(childWrapper);
            if (!parentOval || !childOval) continue;

            var parentRect = parentOval.getBoundingClientRect();
            var childRect = childOval.getBoundingClientRect();

            // Relative to canvas top-left.
            // LTR: connector starts at parent.right, ends at child.left
            // RTL: connector starts at parent.left, ends at child.right
            var startX, endX;
            if (rtl) {
                startX = parentRect.left - canvasRect.left;
                endX = childRect.right - canvasRect.left;
            } else {
                startX = parentRect.right - canvasRect.left;
                endX = childRect.left - canvasRect.left;
            }

            var startY = parentRect.top + parentRect.height / 2 - canvasRect.top;
            var endY = childRect.top + childRect.height / 2 - canvasRect.top;

            // ── Cubic Bezier control points (D3 curveBezierX algorithm) ───
            //
            // This is the EXACT algorithm used by D3.js's curveBezierX
            // (the curve behind d3.linkHorizontal — the standard
            // horizontal-tree link generator used by every D3 tree
            // example on the web). Source:
            //   https://observablehq.com/@d3/curvebezier--dev
            //   https://d3js.org/d3-shape/curve
            //
            // For a cubic Bezier `M sx sy C cp1x cp1y, cp2x cp2y, ex ey`:
            //   cp1 = (ex, sy)  →  control point 1 at END's X, START's Y
            //   cp2 = (sx, ey)  →  control point 2 at START's X, END's Y
            //
            // WHY THIS IS BETTER THAN THE MIDPOINT FORMULA (v5/v6):
            //   The previous version put BOTH control points at the X
            //   midpoint (cp1X = cp2X = midX). That works in LTR but
            //   degenerates to a near-straight diagonal line when the
            //   horizontal gap (dx) is small — because both control
            //   points cluster near the midpoint, giving the curve no
            //   "pull" away from the straight line between the endpoints.
            //   In RTL the gap measurement was sometimes near-zero (the
            //   parent's left edge and the child's right edge ended up
            //   very close after flex layout settled), so every RTL
            //   connector collapsed to a straight diagonal. That was
            //   the "lines are straight in Persian" bug.
            //
            //   The D3 formula is DIRECTION-AGNOSTIC: it puts cp1 at
            //   the END's X coordinate (full horizontal pull toward
            //   the child) and cp2 at the START's X coordinate (full
            //   pull toward the parent). This guarantees:
            //     - The curve ALWAYS exits the parent horizontally
            //       (cp1 shares startY, so the tangent at start is
            //       purely horizontal — pointing toward endX).
            //     - The curve ALWAYS enters the child horizontally
            //       (cp2 shares endY, so the tangent at end is purely
            //       horizontal — pointing toward startX).
            //     - The curve NEVER degenerates to a straight line,
            //       regardless of how small |dx| is. Even with a 1px
            //       gap, the control points are at full opposite X
            //       coordinates, producing a tight but visible S-curve.
            //
            // DIRECTION SYMMETRY:
            //   The formula works identically in LTR and RTL because
            //   it only uses startX/endX (whichever edge faces the
            //   other node). In LTR, startX < endX (parent on left).
            //   In RTL, startX > endX (parent on right). The Bezier
            //   math is symmetric — mirroring startX ↔ endX mirrors
            //   the curve, which is exactly the RTL behavior we want.
            var path =
                'M ' + startX + ' ' + startY +
                ' C ' + endX + ' ' + startY +
                ', ' + startX + ' ' + endY +
                ', ' + endX + ' ' + endY;

            connectors.push({
                parentId: parentId,
                childId: childWrapper.getAttribute('data-node-id'),
                path: path
            });
        }

        // DIAGNOSTIC LOG (gated behind URL flag so it doesn't spam the
        // console in normal use). If connectors still look wrong, open
        // the page with ?debugTree=1 in the URL and check the console —
        // it will print every connector's start/end/control coordinates
        // and the final SVG path string. This lets us see immediately
        // whether the bug is in measurement (wrong coordinates) or in
        // the path formula (right coordinates but bad curve).
        if (typeof URLSearchParams !== 'undefined' &&
            new URLSearchParams(window.location.search).has('debugTree')) {
            console.log('[adminCategoriesTree] rtl=' + rtl +
                ', connectors=' + connectors.length);
            connectors.forEach(function (c, i) {
                console.log('  [' + i + '] ' + c.parentId +
                    ' -> ' + c.childId +
                    '  path=' + c.path);
            });
        }

        return connectors;
    }

    // cssEscape — escapes a string for safe use in a CSS selector.
    // We need this because parent IDs are Guids (no special chars), but
    // being defensive here means a non-Guid ID wouldn't break the
    // querySelector. Uses CSS.escape if available, else a regex fallback.
    function cssEscape(value) {
        if (window.CSS && typeof window.CSS.escape === 'function') {
            return window.CSS.escape(value);
        }
        return value.replace(/(["\\])/g, '\\$1');
    }

    function bindRedraw(canvasEl, dotNetRef) {
        // Defensive: the .NET side may call us on firstRender BEFORE the
        // .tree-canvas element is actually in the DOM (e.g. while the
        // page is still showing the loading spinner). In that case the
        // ElementReference comes through as an empty object that is NOT
        // an instance of Element — ResizeObserver.observe() would throw
        // "parameter 1 is not of type 'Element'" and crash the circuit.
        // Bail out silently; the .NET side will retry on the next render
        // once the canvas is actually present.
        if (!canvasEl || !dotNetRef) return function () { };
        if (!(canvasEl instanceof Element)) return function () { };

        // v6: Idempotent re-binding — disconnect ALL previous observers
        // before creating new ones. This prevents MutationObserver leaks
        // when bindRedraw is called multiple times (e.g. when the user
        // switches between Tree and List views). Each call creates a new
        // MutationObserver on document.documentElement, which is NEVER
        // GC'd (because document.documentElement is never GC'd). Without
        // this cleanup, every view switch would permanently leak one
        // observer, and on language switch all leaked observers would
        // fire and call UpdateConnectors with stale/empty paths,
        // potentially clearing the connector lines.
        //
        // This cleanup does NOT affect the first call (the array is
        // empty), so the tree view's behavior is unchanged on initial
        // render. It only matters on subsequent calls (view switches).
        while (_disconnectFunctions.length > 0) {
            try { _disconnectFunctions.pop()(); } catch (e) { /* ignore */ }
        }

        var redraw = function () {
            try {
                var paths = measureConnectors(canvasEl);
                dotNetRef.invokeMethodAsync('UpdateConnectors', paths);
            } catch (e) {
                // Swallow — Blazor circuit might be disposing. The next
                // render cycle will re-bind.
                console.warn('adminCategoriesTree.redraw failed:', e);
            }
        };

        // Initial measure (after a microtask so the DOM is fully painted).
        Promise.resolve().then(redraw);

        // Re-measure on canvas resize (window resize, sidebar collapse,
        // font load, expand/collapse of a branch, etc.).
        var ro = new ResizeObserver(function () { redraw(); });
        ro.observe(canvasEl);

        // Re-measure when the document direction changes (language switch
        // from en↔fa). ResizeObserver doesn't fire for direction changes,
        // so we use a MutationObserver on the <html> element's attributes.
        var mo = new MutationObserver(function (mutations) {
            for (var i = 0; i < mutations.length; i++) {
                if (mutations[i].attributeName === 'dir') {
                    redraw();
                    return;
                }
            }
        });
        mo.observe(document.documentElement, { attributes: true, attributeFilter: ['dir'] });

        // Return a disconnect function for cleanup. Also register it in
        // the global _disconnectFunctions array so that the NEXT call to
        // bindRedraw can disconnect this observer set before creating a
        // new one (prevents leaks on view-switch).
        var disconnect = function () {
            ro.disconnect();
            mo.disconnect();
        };
        _disconnectFunctions.push(disconnect);
        return disconnect;
    }

    // Expose on window. Use a namespace object so we don't pollute.
    window.adminCategoriesTree = {
        measureConnectors: measureConnectors,
        bindRedraw: bindRedraw
    };
})();
