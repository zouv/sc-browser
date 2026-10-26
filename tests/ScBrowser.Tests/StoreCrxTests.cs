using System.IO;
using ScBrowser.Browser;

namespace ScBrowser.Tests;

/// <summary>
/// 真实商店包的验签回归（可选）：设置环境变量 <c>SCBROWSER_TEST_CRX</c> 指向一个从商店下载的 .crx
/// 且 <c>SCBROWSER_TEST_CRX_ID</c> 给出扩展 ID 时才运行——平时不依赖网络、不拖慢 CI。
/// </summary>
public class StoreCrxTests
{
    [Fact]
    public void 真实商店包应能通过验签并得到原扩展_ID()
    {
        var path = Environment.GetEnvironmentVariable("SCBROWSER_TEST_CRX");
        var expectedId = Environment.GetEnvironmentVariable("SCBROWSER_TEST_CRX_ID");
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(expectedId) || !File.Exists(path))
        {
            return;   // 没有样本就跳过（xUnit 没有运行时 skip，这里直接返回）。
        }

        var info = CrxPackage.Read(path);

        Assert.Equal(expectedId, info.ExtensionId);
        Assert.Equal(3, info.FormatVersion);
    }
}
