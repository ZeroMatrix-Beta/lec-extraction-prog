using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.GenAI.Types;

class Program {
    static void Main() {
        var part = new Part {
            FileData = new FileData {
                FileUri = "gs://test/video.mp4",
                MimeType = "video/mp4"
            },
            VideoMetadata = new VideoMetadata {
                StartOffset = "0s",
                EndOffset = "10s",
                Fps = 0.5
            }
        };

        var options = new JsonSerializerOptions {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        string json = JsonSerializer.Serialize(part, options);
        Console.WriteLine("=== Serialized Part JSON ===");
        Console.WriteLine(json);
    }
}
