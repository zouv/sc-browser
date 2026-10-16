/*
 * sc-browser 内部页面 ↔ C++/C# 宿主的消息桥。
 *
 * 协议（与 C# 侧 WebMessageRouter 一一对应）：
 *   页面 → 宿主：{ type, requestId?, payload }
 *   宿主 → 页面：{ type: "response", requestId, ok, result | error }
 *   宿主 → 页面：{ type: "<事件名>", payload }        （单向事件推送）
 *
 * 用法：
 *   const info = await sc.send('app.info');           // 请求/响应
 *   sc.on('settings.changed', p => { ... });          // 订阅事件
 *   sc.emit('navigation.navigate', { url });          // 单向通知
 */
(function () {
  "use strict";

  var REQUEST_TIMEOUT_MS = 15000;
  var pending = new Map();
  var handlers = new Map();
  var seq = 0;

  function bridge() {
    return window.chrome && window.chrome.webview ? window.chrome.webview : null;
  }

  function available() {
    return bridge() !== null;
  }

  /** 请求/响应：返回 Promise。 */
  function send(type, payload) {
    if (!available()) {
      return Promise.reject(new Error("WebView2 消息桥不可用（页面未运行在 sc-browser 内？）"));
    }

    var requestId = "r" + ++seq;
    return new Promise(function (resolve, reject) {
      var timer = setTimeout(function () {
        if (pending.has(requestId)) {
          pending.delete(requestId);
          reject(new Error("请求超时：" + type));
        }
      }, REQUEST_TIMEOUT_MS);

      pending.set(requestId, {
        resolve: function (v) {
          clearTimeout(timer);
          resolve(v);
        },
        reject: function (e) {
          clearTimeout(timer);
          reject(e);
        },
      });

      bridge().postMessage({ type: type, requestId: requestId, payload: payload || {} });
    });
  }

  /** 单向通知，不等回执。 */
  function emit(type, payload) {
    if (!available()) {
      return;
    }

    bridge().postMessage({ type: type, payload: payload || {} });
  }

  /** 订阅宿主推送的事件；返回取消订阅函数。 */
  function on(type, handler) {
    if (!handlers.has(type)) {
      handlers.set(type, new Set());
    }

    handlers.get(type).add(handler);
    return function () {
      var set = handlers.get(type);
      if (set) {
        set.delete(handler);
      }
    };
  }

  function receive(event) {
    var msg = event.data;
    if (!msg || typeof msg !== "object") {
      return;
    }

    if (msg.type === "response" && msg.requestId) {
      var waiter = pending.get(msg.requestId);
      if (waiter) {
        pending.delete(msg.requestId);
        if (msg.ok) {
          waiter.resolve(msg.result);
        } else {
          waiter.reject(new Error(msg.error || "宿主返回了未知错误"));
        }
      }
      return;
    }

    var set = handlers.get(msg.type);
    if (set) {
      set.forEach(function (h) {
        try {
          h(msg.payload);
        } catch (err) {
          console.error("[sc] 事件处理异常", msg.type, err);
        }
      });
    }
  }

  if (available()) {
    bridge().addEventListener("message", receive);
  } else {
    console.warn("[sc] 未检测到 WebView2 消息桥，页面将以只读方式渲染。");
  }

  /** 轻量提示条。 */
  function toast(text, durationMs) {
    var el = document.createElement("div");
    el.className = "toast";
    el.textContent = text;
    document.body.appendChild(el);
    requestAnimationFrame(function () {
      el.classList.add("show");
    });
    setTimeout(function () {
      el.classList.remove("show");
      setTimeout(function () {
        el.remove();
      }, 250);
    }, durationMs || 1800);
  }

  window.sc = { send: send, emit: emit, on: on, available: available, toast: toast };
})();
