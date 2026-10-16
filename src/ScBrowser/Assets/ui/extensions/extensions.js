// 扩展管理页。
(function () {
  "use strict";

  var listEl = document.getElementById("list");
  var summaryEl = document.getElementById("summary");

  function render(entries) {
    if (!entries || entries.length === 0) {
      listEl.innerHTML = '<div class="list-empty">尚未安装任何扩展。用上方按钮加载。</div>';
      summaryEl.textContent = "0 个扩展";
      return;
    }

    var enabledCount = entries.filter(function (e) { return e.isEnabled; }).length;
    summaryEl.textContent = entries.length + " 个扩展，其中 " + enabledCount + " 个已启用";
    listEl.innerHTML = "";

    entries.forEach(function (entry) {
      var row = document.createElement("div");
      row.className = "list-row extension-row";

      var main = document.createElement("div");
      main.className = "extension-main";

      var name = document.createElement("div");
      name.className = "extension-name truncate";
      name.textContent = entry.name || "(未命名扩展)";

      var id = document.createElement("div");
      id.className = "extension-id truncate";
      id.textContent = entry.id;
      id.title = entry.id;

      main.appendChild(name);
      main.appendChild(id);

      var actions = document.createElement("div");
      actions.className = "extension-actions";

      // 启用开关
      var label = document.createElement("label");
      label.className = "switch";
      label.title = entry.isEnabled ? "点击停用" : "点击启用";

      var checkbox = document.createElement("input");
      checkbox.type = "checkbox";
      checkbox.checked = !!entry.isEnabled;
      checkbox.addEventListener("change", function () {
        sc.send("extensions.setEnabled", { id: entry.id, enabled: checkbox.checked }).catch(function (err) {
          sc.toast("操作失败：" + err.message);
          checkbox.checked = !checkbox.checked;
        });
      });

      var track = document.createElement("span");
      track.className = "switch-track";

      label.appendChild(checkbox);
      label.appendChild(track);
      actions.appendChild(label);

      var removeBtn = document.createElement("button");
      removeBtn.className = "btn btn-danger";
      removeBtn.textContent = "移除";
      removeBtn.addEventListener("click", function () {
        if (!confirm("确定要移除扩展「" + (entry.name || entry.id) + "」吗？")) {
          return;
        }

        sc.send("extensions.remove", { id: entry.id }).catch(function (err) {
          sc.toast("移除失败：" + err.message);
        });
      });
      actions.appendChild(removeBtn);

      row.appendChild(main);
      row.appendChild(actions);
      listEl.appendChild(row);
    });
  }

  function refresh() {
    return sc.send("extensions.list").then(render).catch(function (err) {
      listEl.innerHTML = '<div class="list-empty"></div>';
      listEl.firstChild.textContent = "读取扩展失败：" + err.message;
    });
  }

  // 安装/移除/启停后宿主会推 extensions.changed，据此刷新。
  sc.on("extensions.changed", refresh);

  document.getElementById("install-folder").addEventListener("click", function () {
    sc.send("extensions.installFromFolder")
      .then(function (info) {
        if (info) {
          sc.toast("已安装：" + (info.name || info.id));
        }
      })
      .catch(function (err) {
        sc.toast("安装失败：" + err.message);
      });
  });

  document.getElementById("install-crx").addEventListener("click", function () {
    sc.send("extensions.installFromCrx")
      .then(function (info) {
        if (info) {
          sc.toast("已安装：" + (info.name || info.id));
        }
      })
      .catch(function (err) {
        sc.toast("安装失败：" + err.message);
      });
  });

  document.getElementById("open-folder").addEventListener("click", function () {
    sc.send("extensions.openManagedFolder");
  });

  refresh();
})();
