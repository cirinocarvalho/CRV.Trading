using CRV.Core.Models;
using Xunit;

namespace CRV.Core.Tests.Models;

public class SmtpSettingsTests
{
    private static SmtpSettings Full() => new()
    {
        Host = "smtp.example.com",
        Username = "alerts@example.com",
        Password = "pw",
        FromAddress = "alerts@example.com",
    };

    [Fact]
    public void IsConfigured_WhenEveryFieldSet()
        => Assert.True(Full().IsConfigured);

    // appsettings.json ships FromAddress empty so the repo carries no personal
    // address. That must degrade to "email disabled", not to a send attempt —
    // callers check IsConfigured and skip with a warning.
    [Fact]
    public void NotConfigured_WhenFromAddressEmpty()
    {
        var s = Full();
        s.FromAddress = "";
        Assert.False(s.IsConfigured);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NotConfigured_WhenAnyRequiredFieldBlank(string blank)
    {
        Assert.False(new SmtpSettings { Host = blank, Username = "u", Password = "p", FromAddress = "f" }.IsConfigured);
        Assert.False(new SmtpSettings { Host = "h", Username = blank, Password = "p", FromAddress = "f" }.IsConfigured);
        Assert.False(new SmtpSettings { Host = "h", Username = "u", Password = blank, FromAddress = "f" }.IsConfigured);
        Assert.False(new SmtpSettings { Host = "h", Username = "u", Password = "p", FromAddress = blank }.IsConfigured);
    }

    [Fact]
    public void NotConfigured_ByDefault()
        => Assert.False(new SmtpSettings().IsConfigured);
}
