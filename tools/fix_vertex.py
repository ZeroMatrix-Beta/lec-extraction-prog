import os

file_path = "VertexAutoExtractionSession.cs"
with open(file_path, "r", encoding="utf-8") as f:
    content = f.read()

# Fix 1: ActiveApiProfile
target1 = """        if (_config.ActiveApiProfile == 0) {
            Console.WriteLine($"[AutoExtraction] API-Key: Dedizierter Key für automatisierte Extraktion (API_KEY-automated-content-extraction)");
        }
        else {
            Console.WriteLine($"[AutoExtraction] API-Key: Profil {_config.ActiveApiProfile} (API_KEY-ai-studio-test-project-{_config.ActiveApiProfile})");
        }"""
replacement1 = """        if (string.IsNullOrWhiteSpace(_config.ProjectId)) {
            Console.WriteLine($"[AutoExtraction] API-Projekt: {_config.ProjectId} ({_config.Location})");
        }"""
content = content.replace(target1, replacement1)

# Fix 2: LatexRefinementSession builder
target2 = """                if (_latexRefinementConfig != null) _latexRefinementConfig.UseVertex = false;
                string refinementApiKey = GoogleGenAi.GoogleAiClientBuilder.ResolveApiKeyByName(_latexRefinementConfig?.VertexApiKeyEnvName ?? "API_KEY-latex-refinement") ?? "no-key";
                Client refinementClient = GoogleGenAi.GoogleAiClientBuilder.BuildVertexClient(refinementApiKey);"""
replacement2 = """                if (_latexRefinementConfig != null) _latexRefinementConfig.UseVertex = true;
                Client refinementClient = GoogleGenAi.GoogleAiClientBuilder.BuildVertexClient(_latexRefinementConfig?.VertexProjectId ?? "", _latexRefinementConfig?.VertexLocation ?? "");"""
content = content.replace(target2, replacement2)

with open(file_path, "w", encoding="utf-8") as f:
    f.write(content)

print("Fixes applied.")
