using WinFWManager.Core.Services;

namespace WinFWManager.Tests.Services;

public class CsvTests
{
    [Fact]
    public void Field_PlainText_IsNotQuoted()
    {
        Csv.Field("svchost").Should().Be("svchost");
        Csv.Field(443).Should().Be("443");
    }

    [Fact]
    public void Field_NullOrEmpty_IsEmpty()
    {
        Csv.Field(null).Should().BeEmpty();
        Csv.Field("").Should().BeEmpty();
    }

    [Theory]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    [InlineData("carriage\rreturn", "\"carriage\rreturn\"")]
    [InlineData(" padded ", "\" padded \"")]
    public void Field_SpecialCharacters_AreQuoted(string input, string expected)
    {
        Csv.Field(input).Should().Be(expected);
    }

    [Fact]
    public void Field_EmbeddedQuotes_AreDoubledAndWrapped()
    {
        Csv.Field("say \"hi\"").Should().Be("\"say \"\"hi\"\"\"");
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+cmd")]
    [InlineData("-2")]
    [InlineData("@SUM(A1)")]
    public void Field_FormulaLikeValues_AreNeutralised(string input)
    {
        // Hostnames come from reverse DNS and process names from disk, so an exported
        // value must not be able to execute when the file is opened in a spreadsheet.
        Csv.Field(input).Should().StartWith("'");
    }

    [Fact]
    public void Field_FormulaContainingComma_IsBothNeutralisedAndQuoted()
    {
        Csv.Field("=HYPERLINK(\"a\",\"b\")")
            .Should().Be("\"'=HYPERLINK(\"\"a\"\",\"\"b\"\")\"");
    }

    [Fact]
    public void Row_JoinsFieldsWithCommas()
    {
        Csv.Row("a", 1, null, "b,c").Should().Be("a,1,,\"b,c\"");
    }

    [Fact]
    public void Field_DateTime_UsesSortableInvariantFormat()
    {
        Csv.Field(new DateTime(2026, 8, 9, 14, 5, 6, 789))
            .Should().Be("2026-08-09 14:05:06.789");
    }

    [Fact]
    public void Field_Double_UsesInvariantCultureSoDecimalsSurvive()
    {
        // A comma decimal separator would silently split the field in locales that use one.
        Csv.Field(1.5).Should().Be("1.5");
    }
}
