// 书签管理器：文件夹树 + 搜索 + 编辑对话框 + 右键菜单 + 拖拽移动 + 导入导出。
(function () {
  "use strict";

  var listEl = document.getElementById("list");
  var summaryEl = document.getElementById("summary");
  var newFolderBtn = document.getElementById("new-folder");
  var expandAllBtn = document.getElementById("expand-all");
  var collapseAllBtn = document.getElementById("collapse-all");
  var importBtn = document.getElementById("import");
  var exportBtn = document.getElementById("export");
  var searchEl = document.getElementById("search");

  // 编辑对话框
  var editBackdrop = document.getElementById("edit-backdrop");
  var editTitle = document.getElementById("edit-title");
  var editName = document.getElementById("edit-name");
  var editUrl = document.getElementById("edit-url");
  var editUrlLabel = document.getElementById("edit-url-label");
  var editFolder = document.getElementById("edit-folder");
  var editSaveBtn = document.getElementById("edit-save");
  var editCancelBtn = document.getElementById("edit-cancel");

  // 右键菜单
  var contextMenu = document.getElementById("context-menu");

  var entries = [];
  var collapsed = Object.create(null);
  var searchTerm = "";
  var editingEntry = null; // 编辑中的节点（null = 新建书签）

  // ------------------------------------------------------------------ //
  // 树 / 渲染
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

  /** 搜索时该节点（或子孙）是否命中。 */
  function matches(entry) {
    if (!searchTerm) {
      return true;
    }
    var hay = ((entry.title || "") + " " + entry.url).toLowerCase();
    return hay.indexOf(searchTerm) >= 0;
  }

  function hasMatchingDescendant(byId, nodeId) {
    return entries.some(function (e) {
      return e.parentId === nodeId && (matches(e) || (e.isFolder && hasMatchingDescendant(byId, e.id)));
    });
  }

  function makeRow(entry, depth, byId) {
    var row = document.createElement("div");
    row.className = "list-row bookmark-row" + (entry.isFolder ? " folder-row" : "");
    row.style.paddingLeft = 12 + depth * 20 + "px";
    row.draggable = true;

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
      actions.appendChild(actionButton("打开全部", function () {
        openAll(entry);
      }));
      actions.appendChild(actionButton("新建子文件夹", function () {
        openEditor(null, entry.id);
      }));
    }

    actions.appendChild(actionButton("编辑", function () {
      openEditor(entry, null);
    }));

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

    row.addEventListener("contextmenu", function (e) {
      e.preventDefault();
      e.stopPropagation();
      showContextMenu(e.clientX, e.clientY, entry);
    });

    // 拖拽：把书签拖到文件夹行上 = 移进该文件夹；拖到书签行上 = 移进它的父文件夹
    //（Chrome 支持精确插位；这里是简化版，用「目标父文件夹」近似）。
    row.addEventListener("dragstart", function (e) {
      e.dataTransfer.setData("text/plain", String(entry.id));
      e.dataTransfer.effectAllowed = "move";
    });

    row.addEventListener("dragover", function (e) {
      e.preventDefault();
      e.dataTransfer.dropEffect = "move";
      row.classList.add("drop-target");
    });

    row.addEventListener("dragleave", function () {
      row.classList.remove("drop-target");
    });

    row.addEventListener("drop", function (e) {
      e.preventDefault();
      row.classList.remove("drop-target");
      var id = Number(e.dataTransfer.getData("text/plain"));
      var dragged = entries.find(function (x) { return x.id === id; });
      if (!dragged || dragged.id === entry.id) {
        return;
      }

      // 目标父文件夹：文件夹本身，或书签的父文件夹。
      var targetParent = entry.isFolder ? entry.id : entry.parentId;
      if (targetParent === dragged.parentId) {
        return;
      }

      sc.send("bookmarks.move", { id: dragged.id, parentId: targetParent })
        .then(refresh)
        .catch(function (err) {
          sc.toast("移动失败：" + err.message);
        });
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

    // 搜索时：只显示命中的行（含命中的文件夹整棵展开），并自动展开所有文件夹。
    var filtered = !searchTerm
      ? tree.root.children
      : tree.root.children.filter(function (e) {
          return matches(e) || (e.isFolder && hasMatchingDescendant(tree.byId, e.id));
        });

    if (filtered.length === 0) {
      var none = document.createElement("div");
      none.className = "list-empty";
      none.textContent = "没有匹配的书签";
      listEl.appendChild(none);
      return;
    }

    filtered.forEach(function (entry) {
      renderNode(entry, 0, tree.byId, listEl);
    });
  }

  // ------------------------------------------------------------------ //
  // 右键菜单
  // ------------------------------------------------------------------ //

  function showContextMenu(x, y, entry) {
    contextMenu.textContent = "";
    contextMenu.hidden = false;

    function item(label, handler, danger) {
      var button = document.createElement("button");
      button.className = "context-menu-item" + (danger ? " danger" : "");
      button.textContent = label;
      button.addEventListener("click", function () {
        hideContextMenu();
        handler();
      });
      contextMenu.appendChild(button);
    }

    if (!entry.isFolder) {
      item("在新标签页中打开", function () {
        sc.emit("navigation.openInNewTab", { url: entry.url, active: true });
      });
    } else {
      item("打开文件夹内全部书签", function () {
        openAll(entry);
      });
    }

    item("编辑…", function () {
      openEditor(entry, null);
    });

    if (entry.isFolder) {
      item("新建子文件夹", function () {
        openEditor(null, entry.id);
      });
    }

    item("删除", function () {
      remove(entry);
    }, true);

    contextMenu.style.left = x + "px";
    contextMenu.style.top = y + "px";
  }

  function hideContextMenu() {
    contextMenu.hidden = true;
  }

  document.addEventListener("click", hideContextMenu);
  document.addEventListener("contextmenu", function (e) {
    if (!contextMenu.contains(e.target)) {
      hideContextMenu();
    }
  });

  // ------------------------------------------------------------------ //
  // 编辑对话框（新建/编辑书签与文件夹）
  // ------------------------------------------------------------------ //

  function openEditor(entry, defaultFolderId) {
    editingEntry = entry || null;

    editTitle.textContent = entry
      ? (entry.isFolder ? "编辑文件夹" : "编辑书签")
      : "新建书签";
    editName.value = entry ? entry.title : "";

    // 文件夹没有网址字段；书签编辑可改网址。
    var isFolder = entry ? entry.isFolder : false;
    editUrlLabel.hidden = isFolder;
    editUrl.hidden = isFolder;
    editUrl.value = entry && !isFolder ? entry.url : "";

    var selected = entry ? entry.parentId : (defaultFolderId || 0);
    editFolder.textContent = "";
    addFolderOption("根目录", 0, selected);
    entries
      .filter(function (e) { return e.isFolder; })
      .forEach(function (folder) {
        addFolderOption(folder.title || "未命名文件夹", folder.id, selected);
      });
    editFolder.value = String(selected);

    editBackdrop.hidden = false;
    editName.focus();
    editName.select();
  }

  function addFolderOption(label, id, selected) {
    var option = document.createElement("option");
    option.value = String(id);
    option.textContent = label;
    if (id === selected) {
      option.selected = true;
    }
    editFolder.appendChild(option);
  }

  function closeEditor() {
    editBackdrop.hidden = true;
    editingEntry = null;
  }

  function saveEditor() {
    var name = editName.value.trim() || "未命名";
    var folderId = Number(editFolder.value) || 0;

    if (editingEntry) {
      var url = editingEntry.isFolder ? editingEntry.url : editUrl.value.trim();
      if (!editingEntry.isFolder && !url) {
        sc.toast("网址不能为空");
        return;
      }

      var tasks = [sc.send("bookmarks.edit", { id: editingEntry.id, title: name, url: url })];
      if (editingEntry.parentId !== folderId) {
        tasks.push(sc.send("bookmarks.move", { id: editingEntry.id, parentId: folderId }));
      }
      Promise.all(tasks).then(refresh);
    } else if (editUrl.value.trim()) {
      sc.send("bookmarks.add", { url: editUrl.value.trim(), title: name, parentId: folderId }).then(refresh);
    } else {
      // 无网址 = 新建文件夹（按钮入口就是「新建文件夹」）。
      sc.send("bookmarks.addFolder", { title: name, parentId: folderId }).then(refresh);
    }

    closeEditor();
  }

  editSaveBtn.addEventListener("click", saveEditor);
  editCancelBtn.addEventListener("click", closeEditor);
  editBackdrop.addEventListener("click", function (e) {
    if (e.target === editBackdrop) {
      closeEditor();
    }
  });

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

  function openAll(entry) {
    sc.send("bookmarks.openAll", { id: entry.id }).then(function (result) {
      if (!result || !result.opened) {
        sc.toast("这个文件夹里没有书签");
      }
    });
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

  function importBookmarks() {
    sc.send("bookmarks.importFromFile")
      .then(function (result) {
        if (result === null || result === undefined) {
          return; // 用户取消了文件选择
        }
        sc.toast("已导入 " + result.imported + " 个书签" + (result.imported === 0 ? "（文件中没有可导入的书签）" : ""));
      })
      .catch(function (err) {
        sc.toast("导入失败：" + err.message);
      });
  }

  function exportBookmarks() {
    sc.send("bookmarks.exportToFile")
      .then(function (result) {
        if (result && result.exported !== undefined) {
          sc.toast("已导出 " + result.exported + " 条书签");
        }
      })
      .catch(function (err) {
        sc.toast("导出失败：" + err.message);
      });
  }

  // ------------------------------------------------------------------ //
  // 事件接线
  // ------------------------------------------------------------------ //

  newFolderBtn.addEventListener("click", function () {
    openEditor(null, 0);
  });

  importBtn.addEventListener("click", importBookmarks);
  exportBtn.addEventListener("click", exportBookmarks);

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

  searchEl.addEventListener("input", function () {
    searchTerm = searchEl.value.trim().toLowerCase();
    // 搜索时全部展开，否则命中的子项看不见。
    if (searchTerm) {
      collapsed = Object.create(null);
    }
    render();
  });

  // 书签数据在别处变化（星标/书签栏/其它窗口）→ 宿主推送刷新。
  sc.on("bookmarks.changed", refresh);

  refresh();
})();
