// 扩展管理页。
(function () {
  "use strict";

  var listEl = document.getElementById("list");
  var summaryEl = document.getElementById("summary");

  function render(entries) {
    if (!entries || entries.length === 0) {
      listEl.textContent = "";
      var empty = document.createElement("div");
      empty.className = "list-empty";
      empty.textContent = "尚未安装任何扩展。可以粘贴商店链接安装，或用上方按钮加载本地扩展。";
      listEl.appendChild(empty);
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

      var head = document.createElement("div");
      head.className = "extension-head";

      if (entry.iconDataUrl) {
        var icon = document.createElement("img");
        icon.className = "extension-icon";
        icon.src = entry.iconDataUrl;
        icon.alt = "";
        head.appendChild(icon);
      }

      var names = document.createElement("div");
      names.className = "extension-names";

      var name = document.createElement("div");
      name.className = "extension-name truncate";
      name.textContent = entry.name || "(未命名扩展)";
      names.appendChild(name);

      var id = document.createElement("div");
      id.className = "extension-id truncate";
      id.textContent = [
        entry.version ? "v" + entry.version : null,
        entry.manifestVersion ? "Manifest V" + entry.manifestVersion : null,
        entry.id,
      ].filter(Boolean).join(" · ");
      id.title = entry.id;
      names.appendChild(id);

      head.appendChild(names);
      main.appendChild(head);

      if (entry.manifestVersion && entry.manifestVersion < 3) {
        var mvWarning = document.createElement("div");
        mvWarning.className = "extension-warning";
        mvWarning.textContent = "Manifest V2 扩展：新版 Chromium 已逐步停止支持。";
        main.appendChild(mvWarning);
      }

      if (entry.description) {
        var description = document.createElement("div");
        description.className = "extension-description";
        description.textContent = entry.description;
        main.appendChild(description);
      }

      if (entry.permissions && entry.permissions.length > 0) {
        var perms = document.createElement("div");
        perms.className = "extension-permissions";
        entry.permissions.slice(0, 8).forEach(function (perm) {
          var chip = document.createElement("span");
          chip.className = "permission-chip";
          chip.textContent = perm;
          perms.appendChild(chip);
        });
        if (entry.permissions.length > 8) {
          var more = document.createElement("span");
          more.className = "permission-chip";
          more.textContent = "+" + (entry.permissions.length - 8);
          perms.appendChild(more);
        }
        main.appendChild(perms);
      }

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

      // 固定 / 取消固定：固定的扩展出现在工具栏，未固定的收进「拼图」菜单。
      if (entry.isEnabled) {
        var pinBtn = document.createElement("button");
        pinBtn.className = "btn";
        pinBtn.textContent = entry.isPinned ? "取消固定" : "固定";
        pinBtn.title = entry.isPinned ? "从工具栏移除" : "固定到工具栏";
        pinBtn.addEventListener("click", function () {
          sc.send("extensions.setPinned", { id: entry.id, pinned: !entry.isPinned })
            .catch(function (err) {
              sc.toast("操作失败：" + err.message);
            });
        });
        actions.appendChild(pinBtn);
      }

      if (entry.optionsPath) {
        var optionsBtn = document.createElement("button");
        optionsBtn.className = "btn";
        optionsBtn.textContent = "选项";
        optionsBtn.addEventListener("click", function () {
          sc.emit("navigation.openInNewTab", {
            url: "chrome-extension://" + entry.id + "/" + entry.optionsPath,
            active: true,
          });
        });
        actions.appendChild(optionsBtn);
      }

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

  document.getElementById("install-store").addEventListener("click", function () {
    var input = document.getElementById("store-input");
    var value = input.value.trim();
    if (!value) {
      sc.toast("请先粘贴商店链接或扩展 ID");
      return;
    }

    sc.toast("正在下载并校验扩展…");
    sc.send("extensions.installFromStore", { input: value })
      .then(function (info) {
        if (info) {
          input.value = "";
          sc.toast("已安装：" + (info.name || info.id));
        } else {
          sc.toast("已取消安装");
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
