using System.Linq;
using LectureExtraction.Refinement;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// Covers the two decisions in the Antigravity PDF repair that used to be guesswork: which part
/// of the agent's JSON is the answer, and whether a repair that compiles may replace the original.
///
/// <para>The second is the dangerous one. A repair that dropped half the lecture still compiles,
/// and the loop then copied it over the final .tex - so the check compares against the original
/// instead of trusting the compiler.</para>
/// </summary>
public class AntigravityRepairTests {
    [Fact]
    public void Parser_prefers_output_text() {
        const string json = """{ "output_text": "\\section{A}", "steps": [ { "type": "model_output", "text": "ignored" } ] }""";

        Assert.Equal("\\section{A}", AntigravityResponseParser.ExtractText(json));
    }

    [Fact]
    public void Parser_skips_thought_steps_and_reads_content_parts() {
        const string json = """
            {
              "steps": [
                { "type": "thought", "summary": [ { "text": "**Analyzing the Error** I'm looking at the brackets" } ] },
                { "type": "model_output", "content": [ { "type": "text", "text": "```latex\n\\section{A}\n```" } ] }
              ]
            }
            """;

        string text = AntigravityResponseParser.ExtractText(json);

        Assert.Contains("\\section{A}", text);
        Assert.DoesNotContain("Analyzing", text);
    }

    [Fact]
    public void Parser_reads_parts_and_direct_text_steps() {
        const string json = """
            { "steps": [ { "parts": [ { "text": "one" } ] }, { "text": "two" } ] }
            """;

        string[] lines = AntigravityResponseParser.ExtractText(json).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

        Assert.Equal(["one", "two"], lines);
    }

    [Fact]
    public void Parser_falls_back_to_outputs() {
        const string json = """{ "outputs": [ { "text": "\\section{B}" } ] }""";

        Assert.Equal("\\section{B}", AntigravityResponseParser.ExtractText(json).Trim());
    }

    [Fact]
    public void Parser_returns_empty_when_there_is_no_answer() {
        const string json = """{ "steps": [ { "type": "thought", "text": "thinking only" } ] }""";

        Assert.Equal("", AntigravityResponseParser.ExtractText(json));
    }

    [Fact]
    public void A_repair_that_keeps_the_document_may_replace_it() {
        string original = Lecture(blocks: 10);
        string repaired = original.Replace("\\frac{a}{b", "\\frac{a}{b}");

        Assert.True(LatexRefinementSession.IsSafeRepairReplacement(original, repaired, out _));
    }

    [Fact]
    public void A_truncated_repair_is_rejected_even_though_it_would_compile() {
        string original = Lecture(blocks: 10);
        string truncated = Lecture(blocks: 4);

        Assert.False(LatexRefinementSession.IsSafeRepairReplacement(original, truncated, out string reason));
        Assert.Contains("kürzer", reason);
    }

    [Fact]
    public void A_repair_that_drops_blocks_is_rejected_even_at_full_length() {
        string original = Lecture(blocks: 10);
        // Nearly the same length, but every speech block was turned into a quote.
        string flattened = original.Replace("\\begin{speech}", "\\begin{quote}").Replace("\\end{speech}", "\\end{quote}");

        Assert.False(LatexRefinementSession.IsSafeRepairReplacement(original, flattened, out string reason));
        Assert.Contains("fehlen Blöcke", reason);
    }

    private static string Lecture(int blocks) =>
        string.Concat(Enumerable.Range(1, blocks).Select(i =>
            $"\\begin{{speech}}[00:{i:00}:00 - 00:{i:00}:59]\nHeute schauen wir uns Gruppe {i} an, mit \\frac{{a}}{{b\n\\end{{speech}}\n" +
            $"\\begin{{content}}\n\\section{{Teil {i}}}\nSei $G$ eine Gruppe.\n\\end{{content}}\n"));
}
