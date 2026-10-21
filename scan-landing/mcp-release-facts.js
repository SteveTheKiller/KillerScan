(function () {
  var metadata = document.querySelector(".installer-meta") || document.querySelector(".mcp-installer-meta");
  if (metadata && !metadata.querySelector(".mcp-runtime")) {
    var runtime = document.createElement("span");
    runtime.className = "mcp-runtime";
    runtime.textContent = ".NET 10";
    metadata.insertBefore(runtime, metadata.children[2] || null);
  }

  var sourceTitle = document.title;
  var description = document.querySelector('meta[name="description"]');
  var sourceDescription = description ? description.content : '';
  var store = document.querySelector('.mcp-store');
  var sourceStoreTitle = store ? store.title : '';
  function localizeReleaseFacts() {
    var language = document.documentElement.lang || 'en';
    var dictionary = typeof I18N === 'undefined' ? {} : (I18N[language] || I18N[language.split('-')[0]] || {});
    document.title = dictionary.mcp_page_title || sourceTitle;
    if (description) description.content = dictionary.mcp_page_description || sourceDescription;
    if (store) store.title = dictionary.mcp_store || sourceStoreTitle;
    var version = document.documentElement.dataset.mcpRelease;
    var label = metadata && metadata.querySelector('span:not(.mcp-runtime)');
    if (label) label.textContent = version ? (dictionary.mcp_version || 'Version') + ' ' + version : (dictionary.mcp_check || 'Checking latest release');
  }
  new MutationObserver(localizeReleaseFacts).observe(document.documentElement, {
    attributes: true, attributeFilter: ['lang', 'data-mcp-release']
  });
  localizeReleaseFacts();

  document.querySelectorAll("#limits li").forEach(function (item) {
    if (/public installer/i.test(item.textContent || "")) item.remove();
  });
  var limitSource = document.querySelector("#limits .mcp-limit-source");
  if (limitSource) {
    limitSource.textContent = limitSource.textContent.replace(/\s*The public installer (?:is still being prepared and )?has not been released\.?/i, "");
  }

  fetch("https://api.github.com/repos/SteveTheKiller/KillerMCP/releases/latest?cache=" + Date.now(), {
    cache: "no-store",
    headers: { Accept: "application/vnd.github+json" }
  }).then(function (response) {
    if (!response.ok) throw new Error("release lookup failed");
    return response.json();
  }).then(function (release) {
    var asset = (release.assets || []).find(function (item) { return item.name === "KillerMCP-Setup.exe"; });
    if (!asset) return;
    var version = String(release.tag_name || "").replace(/^v/, "");
    var download = document.querySelector(".mcp-download");
    if (download) download.href = asset.browser_download_url;
    if (metadata) {
      var values = metadata.querySelectorAll("span:not(.mcp-runtime)");
      if (values[0]) values[0].textContent = "Version " + version;
      if (values[1]) values[1].textContent = (asset.size / 1048576).toFixed(1) + " MiB";
      var link = metadata.querySelector("a");
      if (link) link.href = release.html_url;
    }
    document.documentElement.dataset.mcpRelease = version;
  }).catch(function () {});
})();
