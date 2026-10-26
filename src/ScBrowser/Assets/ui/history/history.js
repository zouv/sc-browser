// 历史记录页：按日期分组展示「访问流水」，支持时间范围与关键字筛选。
(function () {
  "use strict";

  var listEl = document.getElementById("list");
  var summaryEl = document.getElementById("summary");
  var searchEl = document.getElementById("search");
  var rangeEl = document.getElementById("range");
  var clearBtn = document.getElementById("clear-all");

  var debounceTimer = null;

  var WEEKDAYS = ["星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六"];

  function startOfDay(date) {
    var d = new Date(date);
    d.setHours(0, 0, 0, 0);
    return d;
  }

  /** 时间范围 → 起始时间（本地零点，转 ISO 交给宿主按 UTC 比较）。 */
  function rangeStart() {
    var today = startOfDay(new Date());
    switch (rangeEl.value) {
      case "today": return today;
      case "7d": return new Date(today.getTime() - 6 * 24 * 3600 * 1000);
      case "30d": return new Date(today.getTime() - 29 * 24 * 3600 * 1000);
      default: return null;
    }
  }

  function dayKey(d) {
    return d.getFullYear() + "-" + (d.getMonth() + 1) + "-" + d.getDate();
  }

  function dayLabel(d) {
    var today = startOfDay(new Date());
    var yesterday = new Date(today.getTime() - 24 * 3600 * 1000);
    if (dayKey(d) === dayKey(today)) {
      return "今天";
    }
    if (dayKey(d) === dayKey(yesterday)) {
      return "昨天";
    }
    return d.getFullYear() + "年" + (d.getMonth() + 1) + "月" + d.getDate() + "日 " + WEEKDAYS[d.getDay()];
  }

  function timeText(d) {
    return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  }

  function appendGroup(entries) {
    var group = document.createElement("section");
    group.className = "history-group";

    var header = document.createElement("div");
    header.className = "history-group-header";
    header.textContent = entries.label + " · " + entries.items.length + " 条";
    group.appendChild(header);

    entries.items.forEach(function (entry) {
      var visited = new Date(entry.visited);

      var row = document.createElement("div");
      row.className = "list-row history-row";

      var time = document.createElement("div");
      time.className = "history-time";
      time.textContent = isNaN(visited.getTime()) ? "" : timeText(visited);

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

      var del = document.createElement("button");
      del.className = "icon-btn row-action";
      del.textContent = "×";
      del.title = "从历史记录中删除该网址的全部记录";
      del.addEventListener("click", function (e) {
        e.stopPropagation();
        if (!confirm("从历史记录中删除这个网址的全部记录？")) {
          return;
        }
        sc.send("history.removeByUrl", { url: entry.url }).then(refresh);
      });

      row.appendChild(time);
      row.appendChild(main);
      row.appendChild(del);

      row.addEventListener("click", function () {
        sc.emit("navigation.navigate", { url: entry.url });
      });

      group.appendChild(row);
    });

    return group;
  }

  function render(visits) {
    listEl.textContent = "";

    if (!visits || visits.length === 0) {
      var empty = document.createElement("div");
      empty.className = "list-empty";
      empty.textContent = "没有匹配的历史记录";
      listEl.appendChild(empty);
      summaryEl.textContent = "0 条记录";
      return;
    }

    summaryEl.textContent = visits.length + " 条访问记录";

    // 宿主已按时间倒序返回；这里只顺序切分成「同一天」的组。
    var groups = [];
    var current = null;

    visits.forEach(function (entry) {
      var d = new Date(entry.visited);
      var key = isNaN(d.getTime()) ? "unknown" : dayKey(d);

      if (!current || current.key !== key) {
        current = {
          key: key,
          label: isNaN(d.getTime()) ? "未知日期" : dayLabel(d),
          items: [],
        };
        groups.push(current);
      }

      current.items.push(entry);
    });

    groups.forEach(function (group) {
      listEl.appendChild(appendGroup(group));
    });
  }

  function refresh() {
    var from = rangeStart();
    var payload = {
      search: searchEl.value.trim(),
      limit: 1000,
    };
    if (from) {
      payload.from = from.toISOString();
    }

    return sc
      .send("history.visits", payload)
      .then(render)
      .catch(function (err) {
        listEl.textContent = "";
        var box = document.createElement("div");
        box.className = "list-empty";
        box.textContent = "读取历史失败：" + err.message;
        listEl.appendChild(box);
      });
  }

  searchEl.addEventListener("input", function () {
    clearTimeout(debounceTimer);
    debounceTimer = setTimeout(refresh, 180);
  });

  rangeEl.addEventListener("change", refresh);

  clearBtn.addEventListener("click", function () {
    if (!confirm("确定要清除全部历史记录吗？此操作不可撤销。")) {
      return;
    }

    sc.send("history.clear").then(refresh);
  });

  refresh();
})();
