// The latest release from GitHub: version, date and the download link (the same idea as the other app
// pages on erik1028.github.io). The HTML carries the version as of the last edit, so a failed or rate-limited request leaves it.
(function () {
  var repo = document.body.getAttribute('data-repo');
  function mb(n) { return Math.round(n / 1048576) + ' MB'; }
  if (repo) {
    fetch('https://api.github.com/repos/' + repo + '/releases?per_page=10')
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (list) {
        var rel = (list || []).filter(function (x) { return !x.draft && !x.prerelease; })[0];
        if (!rel) return;
        var date = rel.published_at ? new Date(rel.published_at).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' }) : '';
        var ver = document.getElementById('ver');
        if (ver) ver.textContent = rel.tag_name + (date ? ' · ' + date : '');
        Array.prototype.forEach.call(document.querySelectorAll('[data-asset]'), function (a) {
          var re = new RegExp(a.getAttribute('data-asset'), 'i');
          var asset = (rel.assets || []).filter(function (x) { return re.test(x.name); })[0];
          if (!asset) return;
          a.href = asset.browser_download_url;
          var size = a.parentNode.parentNode.querySelector('.size');
          if (size) size.textContent = ' (' + mb(asset.size) + ')';
        });
      })
      .catch(function () {});
  }
  // the Copy button next to the VRChat launch option
  Array.prototype.forEach.call(document.querySelectorAll('[data-copy]'), function (b) {
    b.addEventListener('click', function () {
      var text = document.getElementById(b.getAttribute('data-copy')).textContent;
      (navigator.clipboard ? navigator.clipboard.writeText(text) : Promise.reject()).then(function () {
        b.textContent = 'Copied';
        setTimeout(function () { b.textContent = 'Copy'; }, 1600);
      }).catch(function () {});
    });
  });
})();
