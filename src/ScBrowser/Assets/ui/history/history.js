// 历史记录页：按日期分组 + 时间范围/关键字筛选 + 多选删除 + 行菜单（复制链接等）。
(function () {
  "use strict";

  var listEl = document.getElementById("list");
  var summaryEl = document.getElementById("summary");
  var searchEl = document.getElementById("search");
  var rangeEl = document.getElementById("range");
  var clearBtn = document.getElementById("clear-all");
  var clearDataBtn = document.getElementById("clear-data");
  var deleteSelectedBtn = document.getElementById("delete-selected");

  var debounceTimer = null;
  var selected = Object.create(null); // visitId -> true

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

  /** 站点首字母图标：没有 favicon 存储，用域名的首字母圆片（Chrome 的缺省图标同款思路）。 */
  function letterOf(url) {
    try {
      var host = new URL(url).hostname.replace(/^www\./, "");
      return host ? host[0].toUpperCase() : "?";
    } catch (e) {
      return "?";
    }
  }

  function copyLink(url) {
    function fallback() {
      var box = document.createElement("textarea");
      box.value = url;
      document.body.appendChild(box);
      box.select();
      try { document.execCommand("copy"); } catch (e) {}
      document.body.removeChild(box);
    }

    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(url).then(function () {
        sc.toast("链接已复制");
      }, fallback);
    } else {
      fallback();
      sc.toast("链接已复制");
    }
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

      // 多选复选框
      var check = document.createElement("input");
      check.type = "checkbox";
      check.className = "history-check";
      check.checked = !!selected[entry.id];
      check.addEventListener("change", function () {
        if (check.checked) {
          selected[entry.id] = true;
        } else {
          delete selected[entry.id];
        }
        updateSelectionUi();
      });
      row.appendChild(check);

      var icon = document.createElement("span");
      icon.className = "site-icon";
      icon.textContent = letterOf(entry.url);
      row.appendChild(icon);

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

      var menuBtn = document.createElement("button");
      menuBtn.className = "icon-btn row-action";
      menuBtn.textContent = "⋯";
      menuBtn.title = "更多操作";
      menuBtn.addEventListener("click", function (e) {
        e.stopPropagation();
        showRowMenu(menuBtn, entry);
      });

      row.appendChild(time);
      row.appendChild(main);
      row.appendChild(menuBtn);

      row.addEventListener("click", function (e) {
        if (e.target === check || e.target === menuBtn) {
          return;
        }
        sc.emit("navigation.navigate", { url: entry.url });
      });

      group.appendChild(row);
    });

    return group;
  }

  /** 行尾菜单：复制链接 / 删除此条 / 删除该网址全部记录。 */
  function showRowMenu(anchor, entry) {
    var existing = document.querySelector(".row-menu");
    if (existing) {
      existing.remove();
    }

    var menu = document.createElement("div");
    menu.className = "context-menu row-menu";

    function item(label, handler, danger) {
      var button = document.createElement("button");
      button.className = "context-menu-item" + (danger ? " danger" : "");
      button.textContent = label;
      button.addEventListener("click", function () {
        menu.remove();
        handler();
      });
      menu.appendChild(button);
    }

    item("复制链接", function () {
      copyLink(entry.url);
    });
    item("删除此条记录", function () {
      sc.send("history.remove", { id: entry.id }).then(refresh);
    });
    item("删除该网址的全部记录", function () {
      if (!confirm("从历史记录中删除这个网址的全部记录？")) {
        return;
      }
      sc.send("history.removeByUrl", { url: entry.url }).then(refresh);
    }, true);

    var rect = anchor.getBoundingClientRect();
    menu.style.left = Math.min(rect.left, window.innerWidth - 220) + "px";
    menu.style.top = (rect.bottom + 4) + "px";
    document.body.appendChild(menu);
  }

  document.addEventListener("click", function () {
    var existing = document.querySelector(".row-menu");
    if (existing) {
      existing.remove();
    }
  });

  function updateSelectionUi() {
    var count = Object.keys(selected).length;
    deleteSelectedBtn.hidden = count === 0;
    deleteSelectedBtn.textContent = "删除所选（" + count + "）";
  }

  function render(visits) {
    listEl.textContent = "";
    selected = Object.create(null);
    updateSelectionUi();

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

  clearDataBtn.addEventListener("click", function () {
    // Chrome 的历史页「清除浏览数据」入口；本应用的能力在设置页/快捷键对话框。
    sc.emit("navigation.openInNewTab", { url: "browser://settings", active: true });
  });

  deleteSelectedBtn.addEventListener("click", function () {
    var ids = Object.keys(selected).map(Number);
    if (ids.length === 0) {
      return;
    }
    if (!confirm("删除选中的 " + ids.length + " 条历史记录？")) {
      return;
    }

    sc.send("history.removeMany", { ids: ids }).then(refresh);
  });

  refresh();
})();
