namespace ScBrowser.Browser;

/// <summary>
/// 页面安全状态，对应地址栏左侧的锁形图标。
/// 取值语义与 Chromium 的 <c>Security.securityStateChanged</c> CDP 事件一致。
/// </summary>
public enum SecurityState
{
    /// <summary>未知（尚未收到安全事件，或内部页面）。</summary>
    Unknown = 0,

    /// <summary>普通 HTTP，无加密。</summary>
    Neutral,

    /// <summary>HTTPS，证书有效。</summary>
    Secure,

    /// <summary>HTTPS 但存在不安全因素（如混合内容）。</summary>
    Insecure,

    /// <summary>证书或连接存在明确错误。</summary>
    Danger,
}
