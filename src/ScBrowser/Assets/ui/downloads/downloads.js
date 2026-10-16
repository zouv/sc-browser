// 下载内容页。
(function () {
  "use strict";

  var listEl = document.getElementById("list");
  var summaryEl = document.getElementById("summary");
  var clearBtn = document.getElementById("clear-all");

  // 与 C# 侧 Storage.DownloadState 的数值一一对应，勿改序。
  var STATE = { IN_PROGRESS: 0, COMPLETED: 1, CANCELLED: 2, FAILED: 3 };

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

  function render(entries) {
    if (!entries || entries.length === 0) {
      listEl.innerHTML = '<div class="list-empty">还没有下载记录</div>';
      summaryEl.textContent = "0 项";
      return;
    }

    summaryEl.textContent = entries.length + " 项";
    listEl.innerHTML = "";

    entries.forEach(function (entry) {
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
        entry.filePath;

      main.appendChild(name);
      main.appendChild(meta);

      if (entry.state === STATE.IN_PROGRESS) {
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

      if (entry.state === STATE.COMPLETED) {
        var openBtn = document.createElement("button");
        openBtn.className = "btn";
        openBtn.textContent = "打开";
        openBtn.addEventListener("click", function () {
          sc.send("downloads.openFile", { path: entry.filePath });
        });
        actions.appendChild(openBtn);

        var revealBtn = document.createElement("button");
        revealBtn.className = "btn";
        revealBtn.textContent = "所在文件夹";
        revealBtn.addEventListener("click", function () {
          sc.send("downloads.revealInFolder", { path: entry.filePath });
        });
        actions.appendChild(revealBtn);
      }

      row.appendChild(main);
      row.appendChild(actions);
      listEl.appendChild(row);
    });
  }

  function refresh() {
    return sc.send("downloads.query").then(render).catch(function (err) {
      listEl.innerHTML = '<div class="list-empty"></div>';
      listEl.firstChild.textContent = "读取下载记录失败：" + err.message;
    });
  }

  // 宿主在下载进度/状态变化时会推送 downloads.changed，据此实时刷新。
  sc.on("downloads.changed", refresh);

  clearBtn.addEventListener("click", function () {
    // 只清列表，不动磁盘上的文件。
    if (!confirm("清除下载记录列表？已下载的文件不会被删除。")) {
      return;
    }

    sc.send("downloads.clear").then(refresh);
  });

  refresh();
})();
