namespace ScBrowser.Browser;

/// <summary>
/// 「新标签插在哪里」的纯函数（Chrome 的 Ctrl+单击语义）。
/// <para>
/// WebView2 的 <c>NewWindowRequested</c> 不告诉宿主点击位置，只给目标地址；
/// 因此插入位置由发起标签在集合中的下标推出。连续从同一标签打开时，新标签的
/// <see cref="BrowserTab.Opener"/> 都指向该标签，沿链向右跳过它们即可保持打开顺序。
/// </para>
/// </summary>
public static class TabInsertion
{
    /// <summary>
    /// 新标签应插入的下标：发起标签右侧、跳过所有由它连续打开的标签。
    /// 发起标签已不在集合里时返回 <c>null</c>（调用方按追加到末尾处理）。
    /// </summary>
    public static int? RightOfOpener(IReadOnlyList<BrowserTab> tabs, BrowserTab opener)
    {
        var index = -1;
        for (var i = 0; i < tabs.Count; i++)
        {
            if (ReferenceEquals(tabs[i], opener))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return null;
        }

        index++;
        while (index < tabs.Count && ReferenceEquals(tabs[index].Opener, opener))
        {
            index++;
        }

        return index;
    }
}
