import subprocess, sys, os

os.chdir(r"c:\Users\miche\programming\lec-extraction-prog")

# Get the diff for commit 5830a9c, focusing on AiStudioAutoExtractionSession.cs
result = subprocess.run(
    ["git", "--no-pager", "diff", "3ac420a87bcfd32cb03adf4226e5b5689c583e02", "5830a9cdf7320845e1768496338135de56616b40", "--", "AiStudioAutoExtractionSession.cs"],
    capture_output=True, text=True, encoding="utf-8", errors="replace"
)

output_file = r"c:\Users\miche\programming\lec-extraction-prog\git_diff_output.txt"
with open(output_file, "w", encoding="utf-8") as f:
    f.write("=== STDOUT ===\n")
    f.write(result.stdout[:50000] if result.stdout else "(empty)")
    f.write("\n=== STDERR ===\n")
    f.write(result.stderr[:5000] if result.stderr else "(empty)")

print(f"Written to {output_file}")
print(f"Return code: {result.returncode}")
