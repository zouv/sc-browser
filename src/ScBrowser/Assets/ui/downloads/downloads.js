// 下载内容页：搜索过滤 + 暂停/继续/取消 + 打开/所在文件夹/移除/重新下载。
(function () {
  "use strict";

  var listEl = document.getElementById("list");
  var summaryEl = document.getElementById("summary");
  var clearBtn = document.getElementById("clear-all");
  var searchEl = document.getElementById("search");

  // 与 C# 侧 Storage.DownloadState 的数值一一对应，勿改序。
  var STATE = { IN_PROGRESS: 0, COMPLETED: 1, CANCELLED: 2, FAILED: 3, PAUSED: 4 };

  var entries = [];
  var searchTerm = "";

  function formatBytes(bytes) {
    if (!bytes || bytes <= 0) {
      return "未知大小";
    }

    var units = ["B", "KB", "MB", "GB"];
    var i = 0;
    var value = bytes;
    while (value >= 1024 && i < units.length - 1) {
      value /= 1024;
      i++;
    }

    return value.toFixed(i === 0 ? 0 : 1) + " " + units[i];
  }

  function statusText(state) {
    switch (state) {
      case STATE.COMPLETED: return "已完成";
      case STATE.CANCELLED: return "已取消";
      case STATE.FAILED: return "已中断";
      case STATE.PAUSED: return "已暂停";
      default: return "下载中";
    }
  }

  function statusClass(state) {
    if (state === STATE.COMPLETED) {
      return "status status-completed";
    }
    if (state === STATE.FAILED || state === STATE.CANCELLED) {
      return "status status-failed";
    }
    return "status";
  }

  function timeText(value) {
    var d = new Date(value);
    return isNaN(d.getTime())
      ? ""
      : d.toLocaleString([], { year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit" });
  }

  function render() {
    var visible = searchTerm
      ? entries.filter(function (e) {
          return (e.fileName + " " + e.url).toLowerCase().indexOf(searchTerm) >= 0;
        })
      : entries;

    if (visible.length === 0) {
      listEl.textContent = "";
      var empty = document.createElement("div");
      empty.className = "list-empty";
      empty.textContent = searchTerm ? "没有匹配的下载" : "还没有下载记录";
      listEl.appendChild(empty);
      summaryEl.textContent = "0 项";
      return;
    }

    summaryEl.textContent = entries.length + " 项";
    listEl.textContent = "";

    visible.forEach(function (entry) {
      var row = document.createElement("div");
      row.className = "list-row download-row";

      var main = document.createElement("div");
      main.className = "download-main";

      var name = document.createElement("div");
      name.className = "download-name truncate";
      name.textContent = entry.fileName;
      name.title = entry.filePath;

      var meta = document.createElement("div");
      meta.className = "download-meta truncate";
      var sizeText = formatBytes(entry.totalBytes);
      meta.textContent =
        formatBytes(entry.receivedBytes) +
        (entry.totalBytes > 0 ? " / " + sizeText : "") +
        " · " +
        timeText(entry.started) +
        " · " +
        entry.filePath;

      var source = document.createElement("div");
      source.className = "download-url truncate";
      // 来源 URL 是外部文本，一律 textContent。
      source.textContent = entry.url;
      source.title = entry.url;

      main.appendChild(name);
      main.appendChild(meta);
      main.appendChild(source);

      if (entry.state === STATE.IN_PROGRESS || entry.state === STATE.PAUSED) {
        var track = document.createElement("div");
        track.className = "progress-track";
        var fill = document.createElement("div");
        fill.className = "progress-fill";
        var pct = entry.totalBytes > 0 ? (entry.receivedBytes * 100 / entry.totalBytes) : 0;
        fill.style.width = Math.min(100, Math.max(2, pct)) + "%";
        track.appendChild(fill);
        main.appendChild(track);
      }

      var actions = document.createElement("div");
      actions.className = "download-actions";

      var status = document.createElement("span");
      status.className = statusClass(entry.state);
      status.textContent = statusText(entry.state);
      actions.appendChild(status);

      // 进行中：暂停 / 取消；已暂停：继续 / 取消。
      if (entry.state === STATE.IN_PROGRESS || entry.state === STATE.PAUSED) {
        var toggleBtn = makeButton(entry.state === STATE.PAUSED ? "继续" : "暂停", function () {
          sc.send(entry.state === STATE.PAUSED ? "downloads.resume" : "downloads.pause", { id: entry.id });
        });
        actions.appendChild(toggleBtn);

        var cancelBtn = makeButton("取消", function () {
          sc.send("downloads.cancel", { id: entry.id });
        });
        actions.appendChild(cancelBtn);
      }

      if (entry.state === STATE.COMPLETED) {
        actions.appendChild(makeButton("打开", function () {
          sc.send("downloads.openFile", { id: entry.id });
        }));
        actions.appendChild(makeButton("所在文件夹", function () {
          sc.send("downloads.revealInFolder", { id: entry.id });
        }));
      }

      // 已结束的下载可以重新下载（Chrome 的「重新下载」）。
      if (entry.state === STATE.COMPLETED || entry.state === STATE.FAILED || entry.state === STATE.CANCELLED) {
        actions.appendChild(makeButton("重新下载", function () {
          sc.send("downloads.retry", { id: entry.id });
        }));
      }

      // 从列表移除（不删文件）。
      actions.appendChild(makeButton("移除", function () {
        sc.send("downloads.remove", { id: entry.id });
      }));

      row.appendChild(main);
      row.appendChild(actions);
      listEl.appendChild(row);
    });
  }

  function makeButton(label, handler) {
    var button = document.createElement("button");
    button.className = "btn";
    button.textContent = label;
    button.addEventListener("click", handler);
    return button;
  }

  function refresh() {
    return sc.send("downloads.query").then(function (list) {
      entries = list || [];
      render();
    }).catch(function (err) {
      listEl.textContent = "";
      var box = document.createElement("div");
      box.className = "list-empty";
      box.textContent = "读取下载记录失败：" + err.message;
      listEl.appendChild(box);
    });
  }

  // 宿主在下载进度/状态变化时会推送 downloads.changed，据此实时刷新。
  sc.on("downloads.changed", refresh);

  searchEl.addEventListener("input", function () {
    searchTerm = searchEl.value.trim().toLowerCase();
    render();
  });

  clearBtn.addEventListener("click", function () {
    // 只清列表，不动磁盘上的文件。
    if (!confirm("清除下载记录列表？已下载的文件不会被删除。")) {
      return;
    }

    sc.send("downloads.clear").then(refresh);
  });

  refresh();
})();
