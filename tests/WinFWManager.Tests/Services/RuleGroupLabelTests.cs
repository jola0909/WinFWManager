using WinFWManager.Core.Services;

namespace WinFWManager.Tests.Services;

public class RuleGroupLabelTests
{
    [Theory]
    [InlineData("Network Discovery")]
    [InlineData("Core Networking")]
    [InlineData("")]
    public void Humanize_AlreadyReadable_IsUnchanged(string group)
    {
        RuleGroupLabel.Humanize(group).Should().Be(group);
    }

    [Fact]
    public void Humanize_Null_ReturnsEmpty()
    {
        RuleGroupLabel.Humanize(null).Should().BeEmpty();
    }

    [Theory]
    [InlineData(
        "@{MicrosoftWindows.LKG.IrisService_1000.26100.1742.0_x64__cw5n1h2txyewy?ms-resource://MicrosoftWindows.LKG.IrisService/resources/ProductPkgDisplayName}",
        "MicrosoftWindows.LKG.IrisService")]
    [InlineData(
        "@{MicrosoftWindows.Client.AIX_1000.26100.29.0_x64__cw5n1h2txyewy?ms-resource://MicrosoftWindows.Client.AIX/resources/ProductPkgDisplayName}",
        "MicrosoftWindows.Client.AIX")]
    public void Humanize_UwpPackageReference_YieldsPackageName(string group, string expected)
    {
        RuleGroupLabel.Humanize(group).Should().Be(expected);
    }

    [Fact]
    public void Humanize_UnresolvedDllResource_IsLeftAlone()
    {
        // Showing the raw value is deliberate: it signals the group did not resolve
        // rather than inventing a label for it.
        const string raw = "@FirewallAPI.dll,-32752";
        RuleGroupLabel.Humanize(raw).Should().Be(raw);
    }

    [Theory]
    [InlineData("@{")]
    [InlineData("@{}")]
    [InlineData("@{NoQuestionMark}")]
    [InlineData("@{?ms-resource://x}")]
    public void Humanize_MalformedPackageReference_DoesNotThrow(string group)
    {
        var act = () => RuleGroupLabel.Humanize(group);
        act.Should().NotThrow();
    }
}
