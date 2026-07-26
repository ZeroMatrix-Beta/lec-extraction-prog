import zlib
import os

obj_path = r"c:\Users\miche\programming\lec-extraction-prog\.git\objects\58\30a9cdf7320845e1768496338135de56616b40"
with open(obj_path, "rb") as f:
    compressed_data = f.read()

uncompressed_data = zlib.decompress(compressed_data)

with open(r"c:\Users\miche\programming\lec-extraction-prog\git_obj_uncompressed.txt", "wb") as f:
    f.write(uncompressed_data)
