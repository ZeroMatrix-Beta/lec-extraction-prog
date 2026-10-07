using System;

namespace LectureExtraction.Infrastructure;

/// <summary>
/// [AI Context] The single format for an exception inside a console message: <c>TypeName: Message</c>.
/// The type is what tells "the server said no" (ClientError) from "the connection dropped"
/// (HttpRequestException, IOException) when only the log is left; three hand-written variants of
/// this (with ": ", " - " and "Art der Exception: ..., Fehler: ...") had grown across the code.
/// [Human] Einheitliche Darstellung einer Exception in Meldungen: "Typ: Nachricht".
/// </summary>
public static class ExceptionExtensions {
    public static string Describe(this Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}
