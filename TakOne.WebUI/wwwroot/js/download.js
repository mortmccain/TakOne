// =============================================================================
// download.js — client-side file downloads for Blazor Server pages
// -----------------------------------------------------------------------------
// WHY A JS HELPER (not a .NET approach): Blazor Server components can't push
// a file to the browser's download folder from .NET alone — there is no
// HttpResponse to write to inside an interactive circuit. The standard
// pattern is: build the payload in .NET (full access to localized labels,
// culture-invariant number formatting, business data), hand it to a small
// JS function, and let the BROWSER persist it via the classic
// Blob + object-URL + synthetic-click dance. This keeps the data-shaping
// logic testable in C# and the browser-API usage in one tiny audited spot.
//
// API (invoked via IJSRuntime):
//   takDownload.csv(fileName, csvText)   — UTF-8 CSV with BOM (see below)
//   takDownload.text(fileName, text)     — plain UTF-8 text file
//
// BOM RATIONALE (CSV only): Excel sniffs the first bytes to detect the
// encoding; without the BOM it assumes the legacy Windows codepage and
// mojibakes every Persian character. The BOM (EF BB BF) makes Excel open
// the file as UTF-8 and render "ریال" correctly. LibreOffice and Google
// Sheets autodetect UTF-8 either way.
// =============================================================================

window.takDownload = (function () {
    'use strict';

    // Saves `content` as `fileName` using a Blob + object URL + synthetic
    // anchor click, then revokes the URL. `type` is the MIME type.
    function save(fileName, content, type) {
        // Blob accepts a JS string; the browser encodes it as UTF-8 here.
        var blob = new Blob([content], { type: type });
        var url = URL.createObjectURL(blob);

        var anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = fileName;
        anchor.rel = 'noopener';
        // Not appended to the DOM — the click works without it and avoids
        // layout side effects.
        anchor.click();

        // Revoke on the next tick: revoking immediately can cancel the
        // download on some browsers (the click handler hasn't dereferenced
        // the URL yet).
        setTimeout(function () { URL.revokeObjectURL(url); }, 0);
    }

    return {
        // CSV: prepend the UTF-8 BOM so Excel detects the encoding and the
        // Persian text survives the round-trip. The C# side already emits
        // CRLF line endings (RFC 4180).
        csv: function (fileName, csvText) {
            save(fileName, '\uFEFF' + csvText,
                'text/csv;charset=utf-8');
        },

        text: function (fileName, text) {
            save(fileName, text,
                'text/plain;charset=utf-8');
        }
    };
})();
