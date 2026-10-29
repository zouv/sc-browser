// 设置页：加载 → 编辑 → 即时保存；左侧导航随滚动高亮当前分类，支持 #hash 直达。
(function () {
  "use strict";

  var els = {
    theme: document.getElementById("theme"),
    searchTemplate: document.getElementById("search-template"),
    homePage: document.getElementById("home-page"),
    restoreSession: document.getElementById("restore-session"),
    rememberSession: document.getElementById("remember-session"),
    downloadsFolder: document.getElementById("downloads-folder"),
    askWhere: document.getElementById("ask-where"),
    enableExtensions: document.getElementById("enable-extensions"),
    savePasswords: document.getElementById("save-passwords"),
    autofillForms: document.getElementById("autofill-forms"),
    showBookmarksBar: document.getElementById("show-bookmarks-bar"),
    showHomeButton: document.getElementById("show-home-button"),
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
      els.askWhere.checked = !!settings.askWhereToSaveDownloads;
      els.enableExtensions.checked = !!settings.enableExtensions;
      els.savePasswords.checked = settings.savePasswords !== false;
      els.autofillForms.checked = settings.autofillForms !== false;
      els.showBookmarksBar.checked = !!settings.showBookmarksBar;
      els.showHomeButton.checked = settings.showHomeButton !== false;
    });
  }

  function loadInfo() {
    return sc.send("app.info").then(function (info) {
      document.getElementById("about-version").textContent = "版本 " + (info.version || "—");
      document.getElementById("about-copyright").textContent =
        "Copyright © " + new Date().getFullYear() + " ScBrowser 项目（zouv）。基于 Chromium 内核（WebView2），开放源代码。";
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
      askWhereToSaveDownloads: els.askWhere.checked,
      enableExtensions: els.enableExtensions.checked,
      savePasswords: els.savePasswords.checked,
      autofillForms: els.autofillForms.checked,
      showBookmarksBar: els.showBookmarksBar.checked,
      showHomeButton: els.showHomeButton.checked,
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

  ["restoreSession", "rememberSession", "askWhere", "enableExtensions", "savePasswords", "autofillForms", "showBookmarksBar", "showHomeButton"].forEach(function (key) {
    els[key].addEventListener("change", save);
  });

  // ---- 左侧导航：每个分类一个独立页面（hash 路由，Chrome 同款） ----

  var navItems = Array.prototype.slice.call(document.querySelectorAll(".nav-item"));
  var sections = Array.prototype.slice.call(document.querySelectorAll(".settings-section"));
  var pageTitle = document.getElementById("page-title");
  var pageSubtitle = document.getElementById("page-subtitle");

  /** 按 location.hash 显示对应分类页；无 hash / 未知 hash 显示设置首页。 */
  function showSection() {
    var hash = decodeURIComponent(location.hash || "").replace(/^#/, "");
    var target = document.getElementById(hash);
    var showId = target && target.classList.contains("settings-section") ? hash : "home";

    sections.forEach(function (section) {
      section.hidden = section.id !== showId;
    });

    var current = document.getElementById(showId);
    pageTitle.textContent = current.dataset.title || "设置";
    pageSubtitle.textContent = current.dataset.subtitle || "选择一个分类";

    navItems.forEach(function (item) {
      item.classList.toggle("active", item.getAttribute("href") === "#" + showId);
    });
  }

  window.addEventListener("hashchange", showSection);
  showSection();

  // ---- 关于条目的按钮 ----

  // 没有自动更新器，不假装「检查」——直接打开 GitHub Releases 页。
  document.getElementById("check-update").addEventListener("click", function () {
    sc.emit("navigation.openInNewTab", { url: "https://github.com/zouv/sc-browser/releases", active: true });
  });

  document.getElementById("open-extensions").addEventListener("click", function () {
    sc.emit("navigation.openInNewTab", { url: "browser://extensions", active: true });
  });

  // ---- 清除浏览数据 ----

  document.getElementById("clear-now").addEventListener("click", function () {
    var payload = {
      cache: document.getElementById("clear-cache").checked,
      cookies: document.getElementById("clear-cookies").checked,
      storage: document.getElementById("clear-storage").checked,
      passwords: document.getElementById("clear-passwords").checked,
      autofill: document.getElementById("clear-autofill").checked,
    };

    if (!payload.cache && !payload.cookies && !payload.storage && !payload.passwords && !payload.autofill) {
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
