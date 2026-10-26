using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Google.GenAI.Types;
using LectureExtraction.GoogleAi;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;
using File = System.IO.File;

namespace LectureExtraction.Tests;

/// <summary>
/// [AI Context] Covers the two decisions in AttachmentUploader that are reachable without a network
/// call: the mime mapping, and whether a text file is inlined or routed to the upload path
/// (the InlinePrecedingLecTexParts switch of Phase 12).
///
/// <para>The upload branches themselves cannot be tested here - they hit a paid API through
/// ApiRetryPolicy. What is tested is everything that decides *which* branch is taken, because the
/// failure mode that matters is a .tex silently continuing to be inlined when upload mode was asked
/// for, or a system instruction silently being uploaded when it must stay inline.</para>
/// [Human] Testet die Mime-Zuordnung und die Entscheidung "einbetten oder hochladen".
/// </summary>
[Collection(ConsoleTestCollection.Name)]
public class AttachmentUploaderTests {
    private static AttachmentUploader CreateUploader() =>
        new(client: null!, uploadFolder: "", includePaths: [], isAiStudio: true, gcsBucketName: "");

    [Theory]
    [InlineData(".tex", "text/plain")]
    [InlineData(".md", "text/plain")]
    [InlineData(".txt", "text/plain")]
    [InlineData(".mp4", "video/mp4")]
    [InlineData(".png", "image/png")]
    [InlineData(".pdf", "application/pdf")]
    public void ResolveMimeType_maps_supported_extensions(string extension, string expected) {
        Assert.Equal(expected, AttachmentUploader.ResolveMimeType(extension));
    }

    [Theory]
    [InlineData(".cs")]
    [InlineData(".json")]
    [InlineData(".zip")]
    public void ResolveMimeType_leaves_other_text_extensions_unsupported(string extension) {
        // The uploadTextAsFile bypass is deliberately only usable for .tex, .md and .txt (preceding parts
        // and history): every other entry of s_textExtensions has no caller that wants it uploaded, and
        // would reach a null mime type.
        Assert.Null(AttachmentUploader.ResolveMimeType(extension));
    }

    [Fact]
    public async Task Tex_file_is_inlined_when_uploadTextAsFile_is_false() {
        AnsiConsole.Console = new TestConsole();
        string path = WriteTempTex("\\section{Teil 1}");
        try {
            var parts = new List<Part>();
            bool ok = await CreateUploader().UploadAndAttachFileAsync(path, parts, uploadTextAsFile: false);

            Assert.True(ok);
            var part = Assert.Single(parts);
            Assert.Null(part.FileData);
            Assert.Contains("\\section{Teil 1}", part.Text);
            Assert.Contains($"<attached_file name=\"{Path.GetFileName(path)}\">", part.Text);
        }
        finally {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task System_instruction_stays_inline_even_when_upload_is_requested() {
        // asSystemInstruction must win over uploadTextAsFile: a system instruction has to be inline
        // text to take part in the implicit prefix at all, so an uploaded reference would defeat the
        // very caching this switch exists to trade against.
        AnsiConsole.Console = new TestConsole();
        string path = WriteTempTex("\\section{Teil 1}");
        try {
            var parts = new List<Part>();
            bool ok = await CreateUploader().UploadAndAttachFileAsync(
                path, parts, asSystemInstruction: true, uploadTextAsFile: true);

            Assert.True(ok);
            var part = Assert.Single(parts);
            Assert.Null(part.FileData);
            Assert.Contains("\\section{Teil 1}", part.Text);
        }
        finally {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("attach a.pdf, b.png | explain this", new[] { "a.pdf", "b.png" }, "explain this")]
    [InlineData("attach \"D:\\Analysis 2, Woche 3.mp4\" | transcribe", new[] { "D:\\Analysis 2, Woche 3.mp4" }, "transcribe")]
    [InlineData("attach 'x; y.tex'; z.md", new[] { "x; y.tex", "z.md" }, "")]
    [InlineData("attach \"a | b.md\", c.md | q", new[] { "a | b.md", "c.md" }, "q")]
    [InlineData("attach Peter's notes.pdf, b.md", new[] { "Peter's notes.pdf", "b.md" }, "")]
    [InlineData("  /ATTACH notes.md | one | two", new[] { "notes.md" }, "one | two")]
    [InlineData("attach a.md,, ;b.md", new[] { "a.md", "b.md" }, "")]
    [InlineData("attach", new string[0], "")]
    public void ParseAttachCommand_splits_only_outside_quotes(string input, string[] expectedPaths, string expectedPrompt) {
        var command = AttachmentUploader.ParseAttachCommand(input);

        Assert.Equal(expectedPaths, command.FilePaths);
        Assert.Equal(expectedPrompt, command.Prompt);
    }

    [Fact]
    public async Task UploadFilesAsync_takes_a_path_with_commas_and_semicolons_as_one_file() {
        // The extraction used to send every segment as `attach "<path>" | <prompt>` through the chat
        // parser, which split "Analysis 2, Woche 3" into two files that were both "not found".
        AnsiConsole.Console = new TestConsole();
        string dir = Path.Combine(Path.GetTempPath(), "lecx-attach-" + Path.GetRandomFileName(), "Analysis 2, Woche 3");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "notes; part 1.tex");
        File.WriteAllText(path, "\\section{Teil 1}");
        try {
            var (ok, parts) = await CreateUploader().UploadFilesAsync([path]);

            Assert.True(ok);
            var part = Assert.Single(parts);
            Assert.Contains("\\section{Teil 1}", part.Text);

            var (chatOk, prompt, chatParts) = await CreateUploader().ProcessAttachmentsAsync($"attach \"{path}\" | Was steht hier?");
            Assert.True(chatOk);
            Assert.Equal("Was steht hier?", prompt);
            Assert.Single(chatParts);
        }
        finally {
            Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);
        }
    }

    [Fact]
    public async Task UploadFilesAsync_reports_failure_when_no_file_is_found() {
        AnsiConsole.Console = new TestConsole();

        var (ok, parts) = await CreateUploader().UploadFilesAsync([Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".tex")]);

        Assert.False(ok);
        Assert.Empty(parts);
    }

    private static string WriteTempTex(string content) {
        string path = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.tex");
        File.WriteAllText(path, content);
        return path;
    }
}
