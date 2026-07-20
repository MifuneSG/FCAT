using FCAT.Services;
using FluentAssertions;

namespace FCAT.Tests.Services;

public class MotdParserTests
{
    [Fact]
    public void ParseChannelLinks_ExtractsLinks()
    {
        var motd = """Some text <url=joinChannel:-12345>Boost IV</url> more text""";

        var result = MotdParser.ParseChannelLinks(motd);

        result.Should().ContainSingle();
        result[0].Label.Should().Be("Boost IV");
        result[0].Markup.Should().Be("<url=joinChannel:-12345>Boost IV</url>");
    }

    [Fact]
    public void ParseChannelLinks_MultipleLinks()
    {
        var motd = """<url=joinChannel:-1>Boost IV</url> <url=joinChannel:-2>Logi</url>""";

        var result = MotdParser.ParseChannelLinks(motd);

        result.Should().HaveCount(2);
        result[0].Label.Should().Be("Boost IV");
        result[1].Label.Should().Be("Logi");
    }

    [Fact]
    public void ParseChannelLinks_HtmlEncoded_DecodesFirst()
    {
        var motd = "&lt;url=joinChannel:-12345&gt;Boost IV&lt;/url&gt;";

        var result = MotdParser.ParseChannelLinks(motd);

        result.Should().ContainSingle();
        result[0].Label.Should().Be("Boost IV");
    }

    [Fact]
    public void ParseChannelLinks_DuplicateLabel_LastWins()
    {
        var motd = """<url=joinChannel:-1>Boost</url> <url=joinChannel:-2>Boost</url>""";

        var result = MotdParser.ParseChannelLinks(motd);

        result.Should().ContainSingle();
        result[0].Markup.Should().Contain("-2");
    }

    [Fact]
    public void ParseChannelLinks_EmptyLabel_Skipped()
    {
        var motd = """<url=joinChannel:-1>   </url> <url=joinChannel:-2>Boost</url>""";

        var result = MotdParser.ParseChannelLinks(motd);

        result.Should().ContainSingle();
        result[0].Label.Should().Be("Boost");
    }

    [Fact]
    public void ParseChannelLinks_Null_ReturnsEmpty()
    {
        MotdParser.ParseChannelLinks(null!).Should().BeEmpty();
    }

    [Fact]
    public void ParseChannelLinks_Empty_ReturnsEmpty()
    {
        MotdParser.ParseChannelLinks("").Should().BeEmpty();
    }

    [Fact]
    public void ParseChannelLinks_NoLinks_ReturnsEmpty()
    {
        MotdParser.ParseChannelLinks("Just some text with no channel links").Should().BeEmpty();
    }

    [Fact]
    public void ParseChannelLinks_CaseInsensitive()
    {
        var motd = """<URL=joinChannel:-1>Test Channel</URL>""";

        var result = MotdParser.ParseChannelLinks(motd);

        result.Should().ContainSingle();
        result[0].Label.Should().Be("Test Channel");
    }
}
