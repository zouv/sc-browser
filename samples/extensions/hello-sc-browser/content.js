// 示例扩展的内容脚本：在每个页面右下角注入一个角标。
// 作用是让「扩展是否真的在运行」变得肉眼可验证——如果页面上出现这个角标，
// 说明扩展已被 WebView2 加载并执行，而不只是「安装成功了」。
(function () {
  "use strict";

  var ID = "sc-browser-hello-badge";

  function inject() {
    if (document.getElementById(ID)) {
      return;
    }

    var badge = document.createElement("div");
    badge.id = ID;
    badge.textContent = "sc-browser 扩展示例已注入";
    badge.style.cssText = [
      "position:fixed",
      "right:16px",
      "bottom:16px",
      "z-index:2147483647",
      "padding:8px 14px",
      "border-radius:999px",
      "background:#1a73e8",
      "color:#fff",
      "font:500 13px/1.4 'Segoe UI','Microsoft YaHei UI',sans-serif",
      "box-shadow:0 2px 10px rgba(0,0,0,.28)",
      "pointer-events:none",
    ].join(";");

    document.body.appendChild(badge);
    console.log("[hello-sc-browser] 内容脚本已执行");
  }

  if (document.body) {
    inject();
  } else {
    document.addEventListener("DOMContentLoaded", inject);
  }
})();
