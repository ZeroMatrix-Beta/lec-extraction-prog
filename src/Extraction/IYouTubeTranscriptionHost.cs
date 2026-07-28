using System.Collections.Generic;
using System.Threading.Tasks;
using Google.GenAI.Types;
using LectureExtraction.Extraction.Model;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] The two capabilities <see cref="YouTubeTaskRunner"/> needs from the extraction
/// session, expressed as a narrow seam rather than a reference to the whole session class.
///
/// <para>The YouTube pipeline is otherwise self-contained - it builds its own prompts, its own
/// output paths and its own <c>Part.FromUri</c> attachments - but it does need the session to be
/// set up (system instruction loaded, prefix cache primed) and it needs the same
/// segment-to-LaTeX call the video pipeline uses. Keeping those two behind an interface is what
/// lets the runner move out of the session class without dragging the session's mutable state
/// (<c>_systemInstructionText</c>, <c>_historyParts</c>, the token counters) along with it.</para>
/// [Human] Die zwei Fähigkeiten, die der YouTube-Runner von der Extraktions-Session braucht -
/// bewusst schmal gehalten, damit der Runner die Session nicht als Ganzes kennen muss.
/// </summary>
public interface IYouTubeTranscriptionHost {
    /// <summary>Loads the system instruction and history and primes the cache, once per session.</summary>
    Task<bool> EnsureSessionSetupAsync();

    /// <summary>Sends one segment to the model and returns its LaTeX body plus token usage.</summary>
    Task<SegmentTranscript> TranscribeSegmentToLatexAsync(
        string partFile,
        int partNumber,
        string originalFileName,
        string? parsedPrompt,
        List<Part> attachmentParts,
        List<string> previousTexFiles);
}
