using System;
using System.IO;
using System.Linq;
using LectureExtraction.Cli.Commands;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;
using File = System.IO.File;

namespace LectureExtraction.Tests;

/// <summary>
/// 'clean --pdfs': one finished PDF per lecture. Writes to the console, so it joins the console
/// collection with a TestConsole of its own.
/// </summary>
[Collection(ConsoleTestCollection.Name)]
public class CleanPdfsTests : IDisposable {
    private const string Lecture = "02-18-2026-wednesday-week1-Analysis_II";
    private readonly IAnsiConsole _previousConsole = AnsiConsole.Console;
    private readonly string _root = Directory.CreateTempSubdirectory("clean-pdfs-").FullName;
    private readonly string _folder;

    public CleanPdfsTests() {
        AnsiConsole.Console = new TestConsole();
        _folder = Path.Combine(_root, Lecture);
        Directory.CreateDirectory(_folder);
    }

    public void Dispose() {
        AnsiConsole.Console = _previousConsole;
        Directory.Delete(_root, recursive: true);
    }

    private string Write(string name, string content) {
        string path = Path.Combine(_folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void The_lecture_pdf_is_made_from_the_final_copy_and_identical_copies_are_archived() {
        Write($"{Lecture}-all-offset-copy-1.pdf", "final");
        Write($"step4-{Lecture}-all-offset-copy-1-offset-final.pdf", "final");
        Write($"step3-{Lecture}-speech_refined.pdf", "a different, older build");

        var (archived, _, created) = CleanCommand.ProcessPdfs(_folder, dryRun: false);

        Assert.True(created);
        Assert.Equal(2, archived);
        Assert.Equal("final", File.ReadAllText(Path.Combine(_folder, $"{Lecture}.pdf")));
        Assert.Equal(
            new[] { $"{Lecture}.pdf", $"step3-{Lecture}-speech_refined.pdf" },
            Directory.GetFiles(_folder, "*.pdf").Select(Path.GetFileName).Order());
    }

    [Fact]
    public void A_dry_run_changes_nothing() {
        Write($"{Lecture}.pdf", "final");
        Write($"step4-{Lecture}-all-offset-final.pdf", "final");

        var (archived, _, created) = CleanCommand.ProcessPdfs(_folder, dryRun: true);

        Assert.Equal((1, false), (archived, created));
        Assert.Equal(2, Directory.GetFiles(_folder, "*.pdf").Length);
    }
}
