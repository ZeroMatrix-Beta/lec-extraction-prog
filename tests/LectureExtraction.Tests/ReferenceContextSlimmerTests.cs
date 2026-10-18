using LectureExtraction.Configuration;
using LectureExtraction.Extraction;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>ReferenceSlimming: what a preceding part looks like in the reference context.</summary>
public class ReferenceContextSlimmerTests {
    private static string Lines(params string[] lines) => string.Join("\n", lines);

    // The 2026-10-16 behaviour, still the class default for older parts.
    private static readonly ReferenceSlimProfile OldProfile = new ReferenceSlimmingConfig().OlderParts;

    private static readonly ReferenceSlimProfile BareProfile = new() {
        StripCommentsAndHidden = true,
        RemoveEnvironments = ["speech", "overview", "insight", "steps", "note", "student", "pause", "aiNote", "tikzpicture"],
        RemoveCommands = ["inlinemetanote"],
        MarkRemovedEnvironments = false,
    };

    [Fact]
    public void Old_profile_empties_speech_bodies_and_keeps_their_timestamps() {
        string tex = Lines(@"\begin{speech}[00:00:00 - 00:02:22]", "Good morning, everybody.", "Today: metric spaces.", @"\end{speech}",
            @"\begin{content}", "$d(x,y)$", @"\end{content}");

        Assert.Equal(
            Lines(@"\begin{speech}[00:00:00 - 00:02:22]", "% tokenOpt", @"\end{speech}", @"\begin{content}", "$d(x,y)$", @"\end{content}"),
            ReferenceContextSlimmer.Slim(tex, OldProfile));
    }

    [Fact]
    public void Old_profile_replaces_tikz_pictures_by_a_marker() {
        string tex = Lines(@"\begin{center}", @"\begin{tikzpicture}", @"\draw (0,0) -- (1,1);", @"\end{tikzpicture}", @"\end{center}", @"\label{fig:a}");

        Assert.Equal(Lines(@"\begin{center}", "% tokenOpt (tikzpicture)", @"\end{center}", @"\label{fig:a}"), ReferenceContextSlimmer.Slim(tex, OldProfile));
    }

    [Fact]
    public void Several_blocks_are_each_replaced_on_their_own() {
        string tex = Lines(@"\begin{speech}[a]", "one", @"\end{speech}", "keep", @"\begin{speech}[b]", "two", @"\end{speech}");

        Assert.Equal(
            Lines(@"\begin{speech}[a]", "% tokenOpt", @"\end{speech}", "keep", @"\begin{speech}[b]", "% tokenOpt", @"\end{speech}"),
            ReferenceContextSlimmer.Slim(tex, OldProfile));
    }

    [Fact]
    public void Bare_profile_removes_speech_boxes_and_figures_but_keeps_sections_theorems_and_labels() {
        string tex = Lines(
            @"\lecturechapter{Friday}{23 Feb}{23 February 2024}{Symmetric Encryption}",
            "% Final words: And this model...",
            @"\begin{overview}[Context]", "This lecture ...", @"\end{overview}",
            "",
            @"\section{PRGs}\label{sec:prg}",
            "",
            @"\begin{speech}[00:00:00 - 00:00:53]", @"Okay. \inlinemetanote{skips slides} Yes.", @"\end{speech}",
            "",
            @"\begin{content}[Syntax]",
            @"\begin{definition}[PRG]\label[definition]{def:prg}",
            @"A PRG is \inlinemetanote{points at \textit{G}}efficient.",
            @"\end{definition}",
            "",
            @"\begin{steps}[Intuition]", "expander", @"\end{steps}",
            @"\begin{center}", @"\begin{tikzpicture}", @"\draw (0,0);", @"\end{tikzpicture}", @"\end{center}",
            @"\end{content}",
            "",
            @"\begin{hidden}", "- Canvas: x from 0 to 8", @"\end{hidden}",
            @"\begin{insight}[Why]", @"\begin{equation}\label{eq:adv}x\end{equation}", @"\end{insight}",
            "",
            @"\begin{student}", "Question?", @"\end{student}",
            "",
            @"\begin{aiNote}[End of part]", "Board: slide 45", @"\end{aiNote}");

        Assert.Equal(Lines(
                @"\lecturechapter{Friday}{23 Feb}{23 February 2024}{Symmetric Encryption}",
                "",
                @"\section{PRGs}\label{sec:prg}",
                "",
                @"\begin{content}[Syntax]",
                @"\begin{definition}[PRG]\label[definition]{def:prg}",
                @"A PRG isefficient.",
                @"\end{definition}",
                "",
                @"\end{content}",
                "",
                @"\label{eq:adv}",
                "",
                ""),
            ReferenceContextSlimmer.Slim(tex, BareProfile));
    }

    [Fact]
    public void An_unclosed_environment_is_left_alone_and_does_not_swallow_the_next_one() {
        string tex = Lines(@"\begin{speech}[a]", "unclosed", @"\begin{content}", "board", @"\end{content}",
            @"\begin{speech}[b]", "two", @"\end{speech}", "after");

        Assert.Equal(Lines(@"\begin{speech}[a]", "unclosed", @"\begin{content}", "board", @"\end{content}", "", "after", ""),
            ReferenceContextSlimmer.Slim(tex, BareProfile));
    }

    [Fact]
    public void An_empty_profile_changes_nothing() {
        string tex = Lines("% Final words: x", @"\begin{speech}[a]", "one", @"\end{speech}", "", "", "", @"\begin{aiNote}", "b", @"\end{aiNote}");

        Assert.Equal(tex, ReferenceContextSlimmer.Slim(tex, new ReferenceSlimProfile()));
    }

    [Fact]
    public void Rule_text_names_what_each_profile_removes() {
        string rule = ReferenceContextSlimmer.RuleText(BareProfile, new ReferenceSlimProfile { StripCommentsAndHidden = true }, commentsAlreadyStated: true);

        Assert.StartsWith("5. To save tokens, in every context file except the most recent one every `speech`, ", rule);
        Assert.Contains("`aiNote` and `tikzpicture` environment may or may not have been removed entirely", rule);
        Assert.Contains(@"every `\inlinemetanote` may or may not have been removed", rule);
        Assert.DoesNotContain("comments", rule.Split("Wherever")[0]);
    }

    [Fact]
    public void Rule_text_is_empty_when_nothing_is_removed() {
        Assert.Equal("", ReferenceContextSlimmer.RuleText(null, new ReferenceSlimProfile { StripCommentsAndHidden = true }, commentsAlreadyStated: true));
    }

    [Fact]
    public void Rule_text_mentions_the_most_recent_file_when_its_profile_removes_something() {
        string rule = ReferenceContextSlimmer.RuleText(OldProfile, new ReferenceSlimProfile { RemoveEnvironments = ["student"] }, commentsAlreadyStated: true);

        Assert.Contains("in the most recent context file every `student` environment may or may not have been removed entirely", rule);
        Assert.Contains("in every older context file", rule);
    }
}
