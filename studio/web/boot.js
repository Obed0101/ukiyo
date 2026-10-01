// Runs before the first paint (classic script in <head>): applies the last theme and grid this browser saw, so the
// Studio does not flash the default look while the manifest loads. The shell rewrites the cache on every change.
(function applyCachedStudio() {
  try {
    var cache = JSON.parse(localStorage.getItem("ukiyo.studio.cache.v1") || "null");
    if (!cache) return;
    var root = document.documentElement;
    for (var key in cache.vars) root.style.setProperty("--" + key, cache.vars[key]);
    if (cache.colorScheme) root.style.colorScheme = cache.colorScheme;
    if (cache.theme) root.dataset.theme = cache.theme;
  } catch (error) {
    // Storage blocked (private window) or corrupt cache: the stylesheet defaults apply.
  }
})();
