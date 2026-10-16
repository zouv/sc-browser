// 新标签页交互：搜索框提交 + 快速入口。
(function () {
  "use strict";

  var form = document.getElementById("search-form");
  var input = document.getElementById("search-input");

  // 搜索框：把原始输入交给宿主解析（内部页面 / 网址 / 搜索词由宿主统一判定，
  // 避免两端各写一套规则而行为不一致）。
  form.addEventListener("submit", function (e) {
    e.preventDefault();
    var value = input.value.trim();
    if (!value) {
      return;
    }

    sc.emit("navigation.navigate", { input: value });
  });

  // 快速入口：统一走内部页面导航。
  document.getElementById("quick-links").addEventListener("click", function (e) {
    var link = e.target.closest("[data-target]");
    if (!link) {
      return;
    }

    e.preventDefault();
    sc.emit("navigation.navigate", { input: link.dataset.target });
  });
})();
