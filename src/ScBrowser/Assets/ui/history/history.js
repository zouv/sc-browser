// 历史记录页。
(function () {
  "use strict";

  var listEl = document.getElementById("list");
  var summaryEl = document.getElementById("summary");
  var searchEl = document.getElementById("search");
  var clearBtn = document.getElementById("clear-all");

  var debounceTimer = null;

  function formatTime(iso) {
    var d = new Date(iso);
    if (isNaN(d.getTime())) {
      return "";
    }

    var today = new Date();
    var sameDay = d.toDateString() === today.toDateString();
    return sameDay
      ? d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
      : d.toLocaleDateString() + " " + d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  }

  function render(entries) {
    if (!entries || entries.length === 0) {
      listEl.innerHTML = '<div class="list-empty">没有匹配的历史记录</div>';
      summaryEl.textContent = "0 条记录";
      return;
    }

    summaryEl.textContent = entries.length + " 条记录";
    listEl.innerHTML = "";

    entries.forEach(function (entry) {
      var row = document.createElement("div");
      row.className = "list-row history-row";

      var main = document.createElement("div");
      main.className = "history-main";

      var title = document.createElement("div");
      title.className = "history-title truncate";
      // 一律用 textContent：标题与地址来自任意网站，绝不能当 HTML 解析（XSS 风险）。
      title.textContent = entry.title || entry.url;

      var url = document.createElement("div");
      url.className = "history-url truncate";
      url.textContent = entry.url;

      main.appendChild(title);
      main.appendChild(url);

      var meta = document.createElement("div");
      meta.className = "history-meta";

      var time = document.createElement("span");
      time.className = "history-time";
      time.textContent = formatTime(entry.lastVisit);
      meta.appendChild(time);

      if (entry.visitCount > 1) {
        var count = document.createElement("span");
        count.textContent = entry.visitCount + " 次";
        meta.appendChild(count);
      }

      var del = document.createElement("button");
      del.className = "icon-btn row-action";
      del.textContent = "×";
      del.title = "从历史记录中删除";
      del.addEventListener("click", function (e) {
        e.stopPropagation();
        sc.send("history.remove", { id: entry.id }).then(refresh);
      });
      meta.appendChild(del);

      row.appendChild(main);
      row.appendChild(meta);

      // 整行点击即导航。
      row.addEventListener("click", function () {
        sc.emit("navigation.navigate", { url: entry.url });
      });

      listEl.appendChild(row);
    });
  }

  function refresh() {
    return sc
      .send("history.query", { search: searchEl.value.trim(), limit: 500 })
      .then(render)
      .catch(function (err) {
        listEl.innerHTML = '<div class="list-empty"></div>';
        listEl.firstChild.textContent = "读取历史失败：" + err.message;
      });
  }

  searchEl.addEventListener("input", function () {
    clearTimeout(debounceTimer);
    debounceTimer = setTimeout(refresh, 180);
  });

  clearBtn.addEventListener("click", function () {
    if (!confirm("确定要清除全部历史记录吗？此操作不可撤销。")) {
      return;
    }

    sc.send("history.clear").then(refresh);
  });

  refresh();
})();
