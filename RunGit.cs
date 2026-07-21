using System;
using System.Diagnostics;
using System.IO;

class Program {
    static void Main() {
        var process = new Process {
            StartInfo = new ProcessStartInfo {
                FileName = "git",
                Arguments = "--no-pager show 5830a9c",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        
        File.WriteAllText("git_show_result.txt", output + "\n" + error);
    }
}
