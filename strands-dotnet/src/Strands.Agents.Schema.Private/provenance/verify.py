"""Verify pinned source identity and invert every permitted source transformation offline."""

from pathlib import Path
import argparse
import hashlib
import json
import re


def verify(project):
    project = project.resolve()
    manifest = json.loads((project / "provenance/manifest.json").read_text(encoding="utf-8"))
    prefix = "strands-dotnet/src/Strands.Agents.Schema.Private/"
    expected = set()
    counts = {"Schema": 0, "Pointer": 0, "More": 0}
    for row in manifest["OriginalFiles"]:
        if "CandidatePath" not in row:
            continue
        relative = row["CandidatePath"].removeprefix(prefix)
        path = (project / relative).resolve()
        assert path.is_relative_to(project), relative
        raw = path.read_bytes()
        assert len(raw) == row["CandidateBytes"] and hashlib.sha256(raw).hexdigest().upper() == row["CandidateSHA256"], relative
        expected.add(relative)
        original = raw
        if row["Disposition"] == "TransformedActiveCSharp":
            counts[row["SourceSet"]] += 1
            text = raw.decode("utf-8")
            for old, new in manifest["NamespaceTransforms"].items():
                pattern = re.compile(r"(?<![\w.])" + re.escape(new) + r"(?!\w)")
                assert len(pattern.findall(text)) == row["NamespaceReplacements"][old], relative
                text = pattern.sub(old, text)
            if row["SourceSet"] == "Schema" and row["Path"] == "src/JsonSchema/DialectRegistry.cs":
                for value in ("dialect.Id", "dialectId"):
                    changed = "if (_wellKnownDialects?.Contains(" + value + ") == true)"
                    assert text.count(changed) == 1
                    text = text.replace(changed, "if (_wellKnownDialects.Contains(" + value + "))")
            if row["SourceSet"] == "Schema" and row["Path"] == "src/JsonSchema/DotnetCompatibility.cs":
                changed = "internal static class DotnetCompatibility"
                assert text.count(changed) == 1
                text = text.replace(changed, "public static class DotnetCompatibility")
            assert row["OriginalLineEnding"] == "LF" and "\r" not in text
            original = (b"\xef\xbb\xbf" if row["OriginalUTF8BOM"] else b"") + text.encode("utf-8")
        assert len(original) == row["Bytes"] and hashlib.sha256(original).hexdigest().upper() == row["SHA256"], relative
        assert hashlib.sha1(b"blob " + str(len(original)).encode() + b"\0" + original).hexdigest() == row["GitBlob"], relative
    actual = {path.relative_to(project).as_posix() for folder in ("Upstream", "PointerUpstream", "MoreUpstream", "Meta-Schemas")
              for path in (project / folder).rglob("*") if path.is_file()}
    actual.add("LICENSE.MIT")
    assert actual == expected, (sorted(actual - expected), sorted(expected - actual))
    assert counts == {"Schema": 126, "Pointer": 12, "More": 15} and len(expected) == 183
    return {"VerifiedVendorFiles": len(expected), "CSharp": counts, "InverseOriginalSHAAndGitBlob": True,
            "NoNetworkOrOriginalScratchInputs": True, "ProjectRoot": str(project)}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project-root", type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    print(json.dumps(verify(args.project_root), sort_keys=True))
