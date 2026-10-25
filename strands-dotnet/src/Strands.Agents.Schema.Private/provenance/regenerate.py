from pathlib import Path
import argparse
import hashlib
import json
import re

parser = argparse.ArgumentParser(description="Regenerate the pinned private source from captured originals; no network/build invocation.")
parser.add_argument("--original-root", required=True, type=Path)
parser.add_argument("--pointer-root", required=True, type=Path)
parser.add_argument("--more-root", required=True, type=Path)
parser.add_argument("--output-root", required=True, type=Path)
args = parser.parse_args()
roots = {"Schema": args.original_root, "Pointer": args.pointer_root, "More": args.more_root}
manifest = json.loads((Path(__file__).parent / "manifest.json").read_text(encoding="utf-8"))
prefix = "strands-dotnet/src/Strands.Agents.Schema.Private/"
for item in manifest["OriginalFiles"]:
    raw = (roots[item["SourceSet"]] / item["Path"]).read_bytes()
    assert len(raw) == item["Bytes"] and hashlib.sha256(raw).hexdigest().upper() == item["SHA256"], item["Path"]
    assert hashlib.sha1(b"blob " + str(len(raw)).encode() + b"\0" + raw).hexdigest() == item["GitBlob"], item["Path"]
    if "CandidatePath" not in item:
        continue
    result = raw
    if item["Disposition"] == "TransformedActiveCSharp":
        text = raw.decode("utf-8-sig")
        for original, replacement in manifest["NamespaceTransforms"].items():
            pattern = re.compile(r"(?<![\w.])" + re.escape(original) + r"(?!\w)")
            assert len(pattern.findall(text)) == item["NamespaceReplacements"][original]
            text = pattern.sub(replacement, text)
        if item["SourceSet"] == "Schema" and item["Path"] == "src/JsonSchema/DialectRegistry.cs":
            for value in ("dialect.Id", "dialectId"):
                old = "if (_wellKnownDialects.Contains(" + value + "))"
                assert text.count(old) == 1
                text = text.replace(old, "if (_wellKnownDialects?.Contains(" + value + ") == true)")
        if item["SourceSet"] == "Schema" and item["Path"] == "src/JsonSchema/DotnetCompatibility.cs":
            old = "public static class DotnetCompatibility"
            assert text.count(old) == 1
            text = text.replace(old, "internal static class DotnetCompatibility")
        result = text.replace("\r\n", "\n").replace("\r", "\n").encode("utf-8")
    assert hashlib.sha256(result).hexdigest().upper() == item["CandidateSHA256"], item["Path"]
    relative = item["CandidatePath"].removeprefix(prefix)
    target = (args.output_root / relative).resolve()
    assert target.is_relative_to(args.output_root.resolve()), relative
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(result)
print("Verified and regenerated 153 C# files, 19 meta-schema JSON, 10 resx and the MIT license.")
