using System.Threading.Tasks;
using Google.GenAI;
using LectureExtraction.Configuration;
using LectureExtraction.GoogleAi;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] What <see cref="AutoExtractionSession"/> needs to know about the platform it runs on:
/// the client, the names it prints, where uploaded files live and how they are cleaned up, and the
/// client the refinement that follows will use. Everything else - prompts, warm-up, resume, the
/// stream loop - is the same on AI Studio and Vertex and lives in the session.
///
/// <para>Before 2026-10-15 the two backends were two ~1,500-line session classes that had drifted
/// apart (prompt wording, setup flow, resume). The merge kept AI Studio's behaviour, the maintained
/// path, and moved only the real platform differences here.</para>
/// [Human] Was die Extraktions-Session vom Backend (AI Studio oder Vertex) wissen muss.
/// </summary>
public interface IExtractionBackend {
    Client Client { get; }

    /// <summary>Short name for the session title, e.g. "AI Studio".</summary>
    string Name { get; }

    /// <summary>Name in the per-request line, e.g. "Google AI Studio".</summary>
    string ProviderName { get; }

    /// <summary>Prints which key or project the session uses, under source and target.</summary>
    void DescribeEndpoint();

    /// <summary>
    /// False when the system instruction must hold text only; history files then stay out of it
    /// (Vertex sends them through its explicit context cache instead).
    /// </summary>
    bool SystemInstructionAcceptsFiles { get; }

    /// <summary>Deletes files a run left on the platform; run before and after every run.</summary>
    Task CleanUpRemoteFilesAsync();

    /// <summary>The uploader for the audio pre-upload the refinement will read.</summary>
    AttachmentUploader CreateAudioUploader(Client? refinementClient, string folder);

    /// <summary>Whether the refinement after this session runs on Vertex.</summary>
    bool RefinementUsesVertex { get; }

    /// <summary>The client the refinement will use; the audio pre-upload must go to the same project.</summary>
    Client CreateRefinementClient(LatexRefinementSessionConfig refinementConfig);
}
