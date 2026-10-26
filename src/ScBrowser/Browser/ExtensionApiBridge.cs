namespace ScBrowser.Browser;

/// <summary>
/// 扩展弹窗里的 <c>chrome.tabs</c> 兼容层（PoC）。
/// <para>
/// WebView2 不提供 <c>chrome.tabs</c> / <c>chrome.windows</c>——它不是一个完整的浏览器，
/// 没有标签栏，自然也没法让扩展枚举或操作标签。很多弹窗类扩展（广告拦截、翻译、密码管理器）
/// 一打开就要 <c>tabs.query({active:true,currentWindow:true})</c>，没有这个就白屏。
/// </para>
/// <para>
/// 这里的做法：弹窗是我们自己开的 WebView2，在文档创建前注入一段脚本，用
/// <c>chrome.webview.postMessage</c> 与宿主通信，由宿主用真实的标签状态回答。
/// 覆盖 query / getCurrent / create / update 与 <c>windows.getCurrent</c> 的子集。
/// <b>后台 service worker 拿不到这套补丁</b>——只能覆盖我们自己承载的扩展页面。
/// </para>
/// </summary>
public static class ExtensionApiBridge
{
    public const string MessagePrefix = "scb.";

    /// <summary>在扩展页面文档创建前注入的脚本。</summary>
    public const string PolyfillScript = """
        (function () {
          "use strict";
          if (window.__scbTabsPolyfill) { return; }
          window.__scbTabsPolyfill = true;

          var chromeApi = window.chrome;
          if (!chromeApi || !chromeApi.webview) { return; }

          function call(type, payload) {
            return new Promise(function (resolve) {
              var requestId = "scb" + Math.random().toString(36).slice(2);
              function onMessage(event) {
                var data = event.data;
                if (data && data.type === "scb.response" && data.requestId === requestId) {
                  chromeApi.webview.removeEventListener("message", onMessage);
                  resolve(data.result);
                }
              }
              chromeApi.webview.addEventListener("message", onMessage);
              chromeApi.webview.postMessage({ type: type, requestId: requestId, payload: payload || {} });
            });
          }

          function wrap(type, mapper) {
            return function (arg, callback) {
              // MV2 用回调，MV3 用 Promise：两种都支持。
              var promise = call(type, typeof arg === "object" && arg !== null ? arg : {}).then(mapper || function (r) { return r; });
              if (typeof callback === "function") { promise.then(callback); }
              return promise;
            };
          }

          var tabs = chromeApi.tabs || {};
          if (!tabs.query) { tabs.query = wrap("scb.tabs.query"); }
          if (!tabs.getCurrent) { tabs.getCurrent = wrap("scb.tabs.getCurrent"); }
          if (!tabs.create) { tabs.create = wrap("scb.tabs.create"); }
          if (!tabs.update) { tabs.update = wrap("scb.tabs.update"); }
          chromeApi.tabs = tabs;

          var windows = chromeApi.windows || {};
          if (!windows.getCurrent) { windows.getCurrent = wrap("scb.windows.getCurrent"); }
          chromeApi.windows = windows;
        })();
        """;
}
