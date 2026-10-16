// 设置页：加载 → 编辑 → 即时保存。
(function () {
  "use strict";

  var els = {
    theme: document.getElementById("theme"),
    searchTemplate: document.getElementById("search-template"),
    homePage: document.getElementById("home-page"),
    restoreSession: document.getElementById("restore-session"),
    rememberSession: document.getElementById("remember-session"),
    downloadsFolder: document.getElementById("downloads-folder"),
    enableExtensions: document.getElementById("enable-extensions"),
  };

  var current = null;

  function load() {
    return sc.send("settings.get").then(function (settings) {
      current = settings;

      els.theme.value = settings.theme || "System";
      els.searchTemplate.value = settings.searchTemplate || "";
      els.homePage.value = settings.homePage || "";
      els.restoreSession.checked = !!settings.restoreSessionOnStartup;
      els.rememberSession.checked = !!settings.rememberSessionOnExit;
      els.downloadsFolder.value = settings.downloadsFolder || "";
      els.enableExtensions.checked = !!settings.enableExtensions;
    });
  }

  function loadInfo() {
    return sc.send("app.info").then(function (info) {
      document.getElementById("info-version").textContent = info.version;
      document.getElementById("info-userdata").textContent = info.userDataFolder;
      document.getElementById("info-extensions").textContent = info.managedExtensionsFolder;
      document.getElementById("info-db").textContent = info.databaseFile;
      document.getElementById("info-logs").textContent = info.logFolder;
    });
  }

  /** 把当前表单状态写回宿主。 */
  function save() {
    if (!current) {
      return;
    }

    var payload = {
      searchTemplate: els.searchTemplate.value.trim(),
      homePage: els.homePage.value.trim(),
      theme: els.theme.value,
      restoreSessionOnStartup: els.restoreSession.checked,
      rememberSessionOnExit: els.rememberSession.checked,
      downloadsFolder: els.downloadsFolder.value.trim(),
      askWhereToSaveDownloads: !!current.askWhereToSaveDownloads,
      enableExtensions: els.enableExtensions.checked,
    };

    sc.send("settings.update", payload)
      .then(function () {
        current = Object.assign({}, current, payload);
        sc.toast("已保存");
      })
      .catch(function (err) {
        sc.toast("保存失败：" + err.message);
      });
  }

  // 主题切换单独走快捷通道，宿主会立刻换肤。
  els.theme.addEventListener("change", function () {
    sc.send("settings.setTheme", { theme: els.theme.value }).catch(function (err) {
      sc.toast("切换主题失败：" + err.message);
    });
  });

  // 其余控件在变更时统一保存。
  ["searchTemplate", "homePage", "downloadsFolder"].forEach(function (key) {
    els[key].addEventListener("change", save);
  });

  ["restoreSession", "rememberSession", "enableExtensions"].forEach(function (key) {
    els[key].addEventListener("change", save);
  });

  // 清除浏览数据
  document.getElementById("clear-now").addEventListener("click", function () {
    var payload = {
      cache: document.getElementById("clear-cache").checked,
      cookies: document.getElementById("clear-cookies").checked,
      storage: document.getElementById("clear-storage").checked,
    };

    if (!payload.cache && !payload.cookies && !payload.storage) {
      sc.toast("请先选择要清除的内容");
      return;
    }

    if (!confirm("确定要清除所选数据吗？此操作不可撤销。")) {
      return;
    }

    sc.send("app.clearBrowsingData", payload)
      .then(function () {
        sc.toast("已清除所选数据");
      })
      .catch(function (err) {
        sc.toast("清除失败：" + err.message);
      });
  });

  load();
  loadInfo();
})();
