import subprocess

try:
    result = subprocess.run(["git", "--no-pager", "show", "5830a9c"], capture_output=True, text=True)
    with open("git_show_result.txt", "w", encoding="utf-8") as f:
        f.write(result.stdout)
        f.write("\n--ERRORS--\n")
        f.write(result.stderr)
except Exception as e:
    with open("git_show_result.txt", "w", encoding="utf-8") as f:
        f.write(str(e))
