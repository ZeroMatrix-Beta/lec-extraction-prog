using System;
using System.IO;
using LectureExtraction.ConsoleUi;

namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] Loads and caches dummy-part0.tex – a large (~4500 token) Lorem-Ipsum placeholder used as
/// the first reference_context block in implicit-prefix-cache warm-up requests. Being big and constant, it
/// anchors Google's implicit prefix cache on a stable, bit-identical prefix before the video payload.
/// Shared between AiStudioAutoExtractionSession and VertexAutoExtractionSession (2026-07-28) — the two
/// backends' copies were byte-identical, unlike GetStaticPromptBeginning/PrimePrefixCacheAsync,
/// which have real per-backend differences and stay separate.
/// [Human] Lädt und cached dummy-part0.tex: das gemeinsame Platzhalterdokument für konsistentes
/// Prefix-Caching, geteilt zwischen AI Studio und Vertex.
/// </summary>
public static class PrefixCacheAnchor {
    private static string? _dummyPart0Content;

    public static string LoadPrefixCacheAnchorText() {
        if (_dummyPart0Content != null) return _dummyPart0Content;
        string[] candidates = [
            Path.Combine(Directory.GetCurrentDirectory(), "dummy-part0.tex"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dummy-part0.tex")
        ];
        foreach (string path in candidates) {
            if (System.IO.File.Exists(path)) {
                _dummyPart0Content = System.IO.File.ReadAllText(path);
                Ui.Detail($"dummy-part0.tex geladen ({_dummyPart0Content.Length:N0} Bytes) aus: {path}", "Cache-Prefix");
                return _dummyPart0Content;
            }
        }
        Ui.Warn("dummy-part0.tex nicht gefunden – Dummy-Prefix ist leer. Cache-Hit für User-Part möglicherweise nicht möglich.");
        _dummyPart0Content = "% dummy-part0.tex not found";
        return _dummyPart0Content;
    }
}
