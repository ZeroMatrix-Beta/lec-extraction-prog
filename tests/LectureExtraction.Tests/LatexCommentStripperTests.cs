using LectureExtraction.Latex;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// Covers the trimming applied to a part's .tex before it is inlined as reference context for later
/// parts. What must go: comments and `hidden` checkpoints. What must stay: everything LaTeX would
/// render, escaped percent signs, and the document's own paragraph breaks.
/// </summary>
public class LatexCommentStripperTests {
    private static string Strip(string tex) => LatexCommentStripper.StripCommentsAndHiddenBlocks(tex);

    [Fact]
    public void Drops_whole_line_comments_and_the_part_header() {
        string tex = "% ====\n% Step 1/4 - Transcription, part 2 of 4\n% ====\n\\section{Kernels}\n";
        Assert.Equal("\\section{Kernels}\n", Strip(tex));
    }

    [Fact]
    public void Keeps_comment_lines_with_a_listed_prefix() {
        string tex = "% ====\n% Lecturer(s): Dennis Hofheinz\n% Final words: And this model...\n% other\n\\section{Kernels} % trailing\n";
        Assert.Equal("% Lecturer(s): Dennis Hofheinz\n% Final words: And this model...\n\\section{Kernels}\n",
            LatexCommentStripper.StripCommentsAndHiddenBlocks(tex, ["% Lecturer(s):", "% Final words:"]));
    }

    [Fact]
    public void Cuts_trailing_comments_but_keeps_the_code_before_them() {
        Assert.Equal("$a + b$\n", Strip("$a + b$ % the sum\n"));
    }

    [Fact]
    public void Keeps_escaped_percent_signs() {
        Assert.Equal("Only 50\\% of students\n", Strip("Only 50\\% of students\n"));
    }

    [Fact]
    public void Treats_percent_after_a_line_break_as_a_comment() {
        Assert.Equal("a \\\\\n", Strip("a \\\\% comment after a line break\n"));
    }

    [Fact]
    public void Removes_hidden_environments_with_their_content() {
        string tex = "Before.\n\\begin{hidden}\n% timestamp: 00:07:00\n% open_loops: none\n\\end{hidden}\nAfter.\n";
        Assert.Equal("Before.\nAfter.\n", Strip(tex));
    }

    [Fact]
    public void Removes_indented_hidden_environments_without_leaving_a_whitespace_line() {
        string tex = "Before.\n    \\begin{hidden}\n    % note\n    \\end{hidden}\nAfter.\n";
        Assert.Equal("Before.\nAfter.\n", Strip(tex));
    }

    [Fact]
    public void Keeps_paragraph_breaks_but_collapses_the_gaps_removal_leaves() {
        string tex = "First paragraph.\n\nSecond paragraph.\n\n% gone\n\n\nThird.\n";
        Assert.Equal("First paragraph.\n\nSecond paragraph.\n\nThird.\n", Strip(tex));
    }

    [Fact]
    public void Handles_windows_line_endings() {
        Assert.Equal("\\section{A}\ntext\n", Strip("% header\r\n\\section{A}\r\ntext % note\r\n"));
    }

    [Fact]
    public void Leaves_a_file_without_comments_unchanged() {
        string tex = "\\begin{speech}[00:00:00 - 00:01:48]\nOkay, so we start.\n\\end{speech}\n";
        Assert.Equal(tex, Strip(tex));
    }
}
