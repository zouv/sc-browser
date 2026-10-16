// 书签页。
(function () {
  "use strict";

  var listEl = document.getElementById("list");
  var summaryEl = document.getElementById("summary");

  function render(entries) {
    if (!entries || entries.length === 0) {
      listEl.innerHTML = '<div class="list-empty">还没有书签。在页面上按 Ctrl+D 即可收藏。</div>';
      summaryEl.textContent = "0 个书签";
      return;
    }

    summaryEl.textContent = entries.length + " 个书签";
    listEl.innerHTML = "";

    entries.forEach(function (entry) {
      var row = document.createElement("div");
      row.className = "list-row bookmark-row";

      var main = document.createElement("div");
      main.className = "bookmark-main";

      var title = document.createElement("div");
      title.className = "bookmark-title truncate";
      title.textContent = entry.title || entry.url;

      var url = document.createElement("div");
      url.className = "bookmark-url truncate";
      url.textContent = entry.url;

      main.appendChild(title);
      main.appendChild(url);

      var del = document.createElement("button");
      del.className = "icon-btn row-action";
      del.textContent = "×";
      del.title = "删除书签";
      del.addEventListener("click", function (e) {
        e.stopPropagation();
        sc.send("bookmarks.remove", { url: entry.url }).then(refresh);
      });

      row.appendChild(main);
      row.appendChild(del);

      row.addEventListener("click", function () {
        sc.emit("navigation.navigate", { url: entry.url });
      });

      listEl.appendChild(row);
    });
  }

  function refresh() {
    return sc
      .send("bookmarks.query")
      .then(render)
      .catch(function (err) {
        listEl.innerHTML = '<div class="list-empty"></div>';
        listEl.firstChild.textContent = "读取书签失败：" + err.message;
      });
  }

  refresh();
})();
