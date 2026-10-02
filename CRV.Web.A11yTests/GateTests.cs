using Xunit;

namespace CRV.Web.A11yTests;

[Collection(A11yCollection.Name)]
public class GateTests(A11yAppFixture app)
{
    [Fact]
    public async Task ImageWithoutAlt_IsReportedAsViolation()
    {
        var page = await app.Browser.NewPageAsync();
        await page.SetContentAsync("<!doctype html><html lang='en'><head><title>t</title></head><body><main><img src='x.png'></main></body></html>");

        var result = await AxeReport.RunAsync(page);
        var report = AxeReport.Format("gate", result);

        Assert.Contains(result.Violations, v => v.Id == "image-alt");
        Assert.Contains("image-alt", report);
        Assert.Contains("<img src=\"x.png\">", report);
        await page.CloseAsync();
    }
}
