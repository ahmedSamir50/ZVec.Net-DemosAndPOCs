using FluentAssertions;
using PDDM.Shared.Constants;
using PDDM.Shared.Dtos;
using PDDM.Shared.Sse;
using PDDM.Shared.Text;

namespace PDDM.Core.Tests;

public class CitationExtractorTests
{
    [Fact]
    public void Extract_FromUrlLines_DedupesAndUsesBrowseKey()
    {
        var context = """
            Story SPARK-44444
            Url: https://issues.apache.org/jira/browse/SPARK-44444
            Also Url: https://issues.apache.org/jira/browse/SPARK-44444
            Url: https://example.com/docs/page
            """;

        var citations = CitationExtractor.Extract(context);

        citations.Should().HaveCount(2);
        citations.Should().ContainSingle(c => c.Key == "SPARK-44444"
            && c.Url == "https://issues.apache.org/jira/browse/SPARK-44444");
        citations.Should().ContainSingle(c => c.Key == "Link"
            && c.Url == "https://example.com/docs/page");
    }

    [Fact]
    public void Extract_BareBrowseUrl_WithoutUrlPrefix()
    {
        var context = "See https://issues.apache.org/jira/browse/spark-57337 for details.";
        var citations = CitationExtractor.Extract(context);
        citations.Should().ContainSingle()
            .Which.Key.Should().Be("SPARK-57337");
    }

    [Fact]
    public void Extract_Empty_ReturnsEmpty()
        => CitationExtractor.Extract(null).Should().BeEmpty();
}

public class ChatAnswerHtmlTests
{
    [Fact]
    public void Format_MarkdownLink_KeepsCustomText()
    {
        var html = ChatAnswerHtml.Format("See [Apache Spark](https://spark.apache.org/) docs.");
        html.Should().Contain(">Apache Spark</a>");
        html.Should().Contain("href=\"https://spark.apache.org/\"");
        html.Should().NotContain("[Apache Spark]");
    }

    [Fact]
    public void Format_BareJiraUrl_ShowsIssueKey()
    {
        var html = ChatAnswerHtml.Format(
            "Ticket https://issues.apache.org/jira/browse/SPARK-44444 closed.");
        html.Should().Contain(">SPARK-44444</a>");
        html.Should().NotContain("https://issues.apache.org/jira/browse/SPARK-44444</a>");
    }

    [Fact]
    public void Format_OtherBareUrl_ShowsLink()
    {
        var html = ChatAnswerHtml.Format("Read https://example.com/a/very/long/path?q=1 now.");
        html.Should().Contain(">Link</a>");
        html.Should().Contain("href=\"https://example.com/a/very/long/path?q=1\"");
    }

    [Fact]
    public void Format_EscapesHtml_NoXssFromModelText()
    {
        var html = ChatAnswerHtml.Format("<script>alert(1)</script> & more");
        html.Should().NotContain("<script>");
        html.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt;");
        html.Should().Contain("&amp; more");
    }
}

public class PromptPackageSseParserTests
{
    private readonly SseEventParser _sut = new();

    [Fact]
    public void Parse_Prompt_DeserializesPackageAndCitations()
    {
        var json = """
            {
              "SystemPrompt": "sys",
              "UserPrompt": "CONTEXT:\nctx\nQUESTION:\nq",
              "Context": "ctx",
              "Citations": [
                { "Key": "SPARK-1", "Url": "https://issues.apache.org/jira/browse/SPARK-1" }
              ]
            }
            """;

        var result = _sut.Parse(SseEventTypes.Prompt, json) as PromptPackageEventDto;
        result.Should().NotBeNull();
        result!.SystemPrompt.Should().Be("sys");
        result.UserPrompt.Should().Contain("QUESTION:");
        result.Context.Should().Be("ctx");
        result.Citations.Should().ContainSingle(c => c.Key == "SPARK-1");
    }
}
