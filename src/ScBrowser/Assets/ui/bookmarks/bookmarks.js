// 书签页：文件夹树 + 重命名 / 移动 / 删除。
(function () {
  "use strict";

  var listEl = document.getElementById("list");
  var summaryEl = document.getElementById("summary");
  var newFolderBtn = document.getElementById("new-folder");
  var expandAllBtn = document.getElementById("expand-all");
  var collapseAllBtn = document.getElementById("collapse-all");

  var entries = [];
  var collapsed = Object.create(null);

  // ------------------------------------------------------------------ //
  // 渲染
  // ------------------------------------------------------------------ //

  function buildTree() {
    var byId = Object.create(null);
    var root = { id: 0, children: [] };

    entries.forEach(function (entry) {
      entry.children = [];
      byId[entry.id] = entry;
    });

    entries.forEach(function (entry) {
      var parent = byId[entry.parentId];
      (parent ? parent.children : root.children).push(entry);
    });

    return { root: root, byId: byId };
  }

  function descendantsOf(byId, nodeId) {
    var result = [];
    var stack = [nodeId];
    while (stack.length > 0) {
      var id = stack.pop();
      entries.forEach(function (entry) {
        if (entry.parentId === id) {
          result.push(entry.id);
          if (entry.isFolder) {
            stack.push(entry.id);
          }
        }
      });
    }
    return result;
  }

  function makeRow(entry, depth, byId) {
    var row = document.createElement("div");
    row.className = "list-row bookmark-row" + (entry.isFolder ? " folder-row" : "");
    row.style.paddingLeft = 12 + depth * 20 + "px";

    var icon = document.createElement("span");
    icon.className = "bookmark-icon";
    icon.textContent = entry.isFolder ? (collapsed[entry.id] ? "▸" : "▾") : "•";
    row.appendChild(icon);

    var main = document.createElement("div");
    main.className = "bookmark-main";

    var title = document.createElement("div");
    title.className = "bookmark-title truncate";
    // 标题可能来自任意网页，一律 textContent。
    title.textContent = entry.title || (entry.isFolder ? "未命名文件夹" : entry.url);

    main.appendChild(title);

    if (!entry.isFolder) {
      var url = document.createElement("div");
      url.className = "bookmark-url truncate";
      url.textContent = entry.url;
      main.appendChild(url);
    } else if (entry.children.length > 0) {
      var count = document.createElement("div");
      count.className = "bookmark-url";
      count.textContent = entry.children.length + " 项";
      main.appendChild(count);
    }

    row.appendChild(main);

    var actions = document.createElement("div");
    actions.className = "bookmark-actions";

    if (entry.isFolder) {
      actions.appendChild(actionButton("新建子文件夹", function () {
        createFolder(entry.id);
      }));
    }

    actions.appendChild(actionButton("重命名", function () {
      rename(entry);
    }));

    if (!entry.isFolder) {
      actions.appendChild(actionButton("移动到…", function () {
        showMovePicker(row, actions, entry, byId);
      }));
    }

    actions.appendChild(actionButton("删除", function () {
      remove(entry);
    }, true));

    row.appendChild(actions);

    row.addEventListener("click", function (e) {
      if (e.target !== row && e.target !== title && e.target !== icon && e.target !== main) {
        return;
      }

      if (entry.isFolder) {
        collapsed[entry.id] = !collapsed[entry.id];
        render();
      } else {
        sc.emit("navigation.navigate", { url: entry.url });
      }
    });

    return row;
  }

  function actionButton(label, handler, danger) {
    var btn = document.createElement("button");
    btn.className = "btn btn-small" + (danger ? " btn-danger" : "");
    btn.textContent = label;
    btn.addEventListener("click", function (e) {
      e.stopPropagation();
      handler();
    });
    return btn;
  }

  function renderNode(entry, depth, byId, container) {
    container.appendChild(makeRow(entry, depth, byId));

    if (entry.isFolder && !collapsed[entry.id]) {
      var children = document.createElement("div");
      children.className = "bookmark-children";
      entry.children.forEach(function (child) {
        renderNode(child, depth + 1, byId, children);
      });
      container.appendChild(children);
    }
  }

  function render() {
    listEl.textContent = "";

    var tree = buildTree();
    var folders = entries.filter(function (e) { return e.isFolder; }).length;
    var bookmarks = entries.length - folders;

    summaryEl.textContent = bookmarks + " 个书签 · " + folders + " 个文件夹";

    if (entries.length === 0) {
      var empty = document.createElement("div");
      empty.className = "list-empty";
      empty.textContent = "还没有书签。在页面上按 Ctrl+D 即可收藏。";
      listEl.appendChild(empty);
      return;
    }

    tree.root.children.forEach(function (entry) {
      renderNode(entry, 0, tree.byId, listEl);
    });
  }

  // ------------------------------------------------------------------ //
  // 操作
  // ------------------------------------------------------------------ //

  function refresh() {
    return sc
      .send("bookmarks.query")
      .then(function (list) {
        entries = list || [];
        render();
      })
      .catch(function (err) {
        listEl.textContent = "";
        var box = document.createElement("div");
        box.className = "list-empty";
        box.textContent = "读取书签失败：" + err.message;
        listEl.appendChild(box);
      });
  }

  function createFolder(parentId) {
    var name = window.prompt("文件夹名称", "新建文件夹");
    if (name === null) {
      return;
    }

    sc.send("bookmarks.addFolder", { title: name.trim() || "新建文件夹", parentId: parentId || 0 })
      .then(refresh);
  }

  function rename(entry) {
    var name = window.prompt("名称", entry.title);
    if (name === null) {
      return;
    }

    sc.send("bookmarks.rename", { id: entry.id, title: name.trim() }).then(refresh);
  }

  function remove(entry) {
    var message = entry.isFolder
      ? "删除文件夹「" + (entry.title || "未命名") + "」及其中的全部内容？"
      : "删除书签「" + (entry.title || entry.url) + "」？";
    if (!window.confirm(message)) {
      return;
    }

    sc.send("bookmarks.removeById", { id: entry.id }).then(refresh);
  }

  /** 行内「移动到…」：把操作区换成一个文件夹下拉 + 确定/取消。 */
  function showMovePicker(row, actions, entry, byId) {
    var excluded = descendantsOf(byId, entry.id);
    var picker = document.createElement("select");
    picker.className = "input move-picker";

    var rootOption = document.createElement("option");
    rootOption.value = "0";
    rootOption.textContent = "根目录";
    picker.appendChild(rootOption);

    entries
      .filter(function (e) { return e.isFolder && excluded.indexOf(e.id) < 0; })
      .forEach(function (folder) {
        var option = document.createElement("option");
        option.value = String(folder.id);
        option.textContent = folder.title || "未命名文件夹";
        picker.appendChild(option);
      });

    picker.value = String(entry.parentId);

    var original = actions;
    row.replaceChild(picker, actions);

    function cleanup() {
      if (picker.parentNode === row) {
        row.replaceChild(original, picker);
      }
    }

    picker.addEventListener("click", function (e) { e.stopPropagation(); });
    picker.addEventListener("change", function () {
      sc.send("bookmarks.move", { id: entry.id, parentId: Number(picker.value) })
        .then(refresh)
        .catch(function (err) {
          sc.toast("移动失败：" + err.message);
          cleanup();
        });
    });
    picker.addEventListener("blur", cleanup);
  }

  // ------------------------------------------------------------------ //

  newFolderBtn.addEventListener("click", function () {
    createFolder(0);
  });

  expandAllBtn.addEventListener("click", function () {
    collapsed = Object.create(null);
    render();
  });

  collapseAllBtn.addEventListener("click", function () {
    entries.forEach(function (entry) {
      if (entry.isFolder) {
        collapsed[entry.id] = true;
      }
    });
    render();
  });

  refresh();
})();
