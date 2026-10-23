// Small UI helpers used through JS interop from the Blazor components.
window.aiCeo = {
    // Keeps the execution feed pinned to the newest entry.
    scrollFeed: function () {
        var el = document.getElementById('feed');
        if (el) {
            el.scrollTop = el.scrollHeight;
        }
    },

    // Removes the "starting…" placeholder once Blazor has rendered.
    hideBoot: function () {
        var boot = document.getElementById('boot');
        if (boot && boot.parentNode) {
            boot.parentNode.removeChild(boot);
        }
    }
};

// BlazorWebView autostarts via the script tag; this only clears the splash text.
window.addEventListener('DOMContentLoaded', function () {
    var observer = new MutationObserver(function () {
        if (document.querySelector('.shell')) {
            window.aiCeo.hideBoot();
            observer.disconnect();
        }
    });
    observer.observe(document.body, { childList: true, subtree: true });
});
