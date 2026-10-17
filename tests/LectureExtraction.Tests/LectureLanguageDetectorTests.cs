using System.IO;
using LectureExtraction.Configuration;
using LectureExtraction.Latex;
using Xunit;

namespace LectureExtraction.Tests;

public class LectureLanguageDetectorTests {
    [Theory]
    [InlineData("% Primary Language: English\n% End of the video: 01:20:31\n\\lecturechapter{...}", LectureLanguage.English)]
    [InlineData("% Primary Language: German\n\\begin{speech}...", LectureLanguage.German)]
    [InlineData("   %Primary Language:   deutsch", LectureLanguage.German)]
    public void FromHeader_ReadsThePrimaryLanguageLine(string tex, LectureLanguage expected) {
        Assert.Equal(expected, LectureLanguageDetector.Detect(tex));
    }

    [Fact]
    public void Header_WinsOverTheWords() {
        string tex = "% Primary Language: English\n" + Repeat("Das ist die Menge der Zahlen und wir zeigen es nicht. ", 20);
        Assert.Equal(LectureLanguage.English, LectureLanguageDetector.Detect(tex));
    }

    [Fact]
    public void FromWords_RecognisesGermanSpeech() {
        string tex = "\\begin{speech}\n" + Repeat("Also, das ist jetzt die Definition, und wir sehen, dass es nicht reicht. ", 10) + "\\end{speech}";
        Assert.Equal(LectureLanguage.German, LectureLanguageDetector.Detect(tex));
    }

    [Fact]
    public void FromWords_RecognisesEnglishSpeech() {
        string tex = "\\begin{speech}\n" + Repeat("So, this is the definition, and we see that it is not enough for the proof. ", 10) + "\\end{speech}";
        Assert.Equal(LectureLanguage.English, LectureLanguageDetector.Detect(tex));
    }

    [Fact]
    public void FromWords_IgnoresCommandNames() {
        // \theorem, \in, \to, \the are commands, not English words
        string tex = Repeat("\\begin{theorem} $x \\in A \\to B$ \\the \\end{theorem} ", 30);
        Assert.Equal(LectureLanguage.Unknown, LectureLanguageDetector.Detect(tex));
    }

    [Fact]
    public void FromWords_IsUnknownForShortText() {
        Assert.Equal(LectureLanguage.Unknown, LectureLanguageDetector.Detect("the and is"));
    }

    [Fact]
    public void ResolvePreamblePath_FallsBackWhenTheLanguageFileIsMissing() {
        string dir = Path.Combine(Path.GetTempPath(), "lecx-preamble-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try {
            string en = Path.Combine(dir, "en.tex");
            File.WriteAllText(en, "%");
            var config = new PdfCompilationConfig {
                PreamblePath = "fallback.tex",
                PreamblePathEnglish = en,
                PreamblePathGerman = Path.Combine(dir, "missing-de.tex"),
            };
            Assert.Equal(en, config.ResolvePreamblePath(LectureLanguage.English));
            Assert.Equal("fallback.tex", config.ResolvePreamblePath(LectureLanguage.German));
            Assert.Equal("fallback.tex", config.ResolvePreamblePath(LectureLanguage.Unknown));
        }
        finally {
            Directory.Delete(dir, true);
        }
    }

    private static string Repeat(string s, int n) => string.Concat(System.Linq.Enumerable.Repeat(s, n));
}
