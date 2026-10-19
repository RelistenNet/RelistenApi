"""Export scoped artist display context for composition inference.

The frozen music catalogue supplies names, not authorship or external artist
identities. A cited catalogue cell preserves the display name even when later
reader snapshots contain another spelling. No live reader or provider is read.
"""

import argparse
from contextlib import closing
import gzip
import hashlib
import json
from pathlib import Path
import shutil
import sqlite3
import sys

from music_catalogue import CatalogueBuildPaused
from music_consolidation import sha256_file


class CompositionArtistContextExporter:
    """Publish a small context manifest last, without replacing an existing run."""

    def __init__(self, music_input_manifest, music_output_manifest, destination, *,
                 input_manifest_sha256, output_manifest_sha256,
                 minimum_free_bytes=int(5.5 * 1024**3), maximum_bytes=16 * 1024**2):
        self.input_manifest = Path(music_input_manifest).resolve(strict=True)
        self.output_manifest = Path(music_output_manifest).resolve(strict=True)
        self.destination = Path(destination).resolve()
        self.input_sha = input_manifest_sha256
        self.output_sha = output_manifest_sha256
        self.minimum_free_bytes = minimum_free_bytes
        self.maximum_bytes = maximum_bytes

    def export(self):
        inputs, input_artifact = self._manifest(self.input_manifest, self.input_sha)
        outputs, output_artifact = self._manifest(self.output_manifest, self.output_sha)
        if (inputs.get("version") != "music-consolidation-input-v1" or inputs.get("state") != "completed"
                or outputs.get("version") != "music-consolidation-output-v1" or outputs.get("complete") is not True
                or outputs.get("inputManifestSha256") != self.input_sha
                or outputs.get("logicalRows") != inputs["descriptors"]["rows"]):
            raise ValueError("Artist context requires the matching completed music input and output")

        catalogue = Path(inputs["catalogue"]["path"]).resolve(strict=True)
        catalogue_stamp = self._stamp(catalogue)
        if (catalogue.stat().st_size != inputs["catalogue"]["bytes"]
                or sha256_file(catalogue) != inputs["catalogue"]["sha256"]):
            raise ValueError("The catalogue differs from the frozen music input")
        self.destination.mkdir(parents=True, exist_ok=False)
        self._space()

        # The pin names this completed database file, not a later live WAL.
        with closing(sqlite3.connect(catalogue.as_uri() + "?mode=ro&immutable=1", uri=True)) as db:
            db.row_factory = sqlite3.Row
            db.execute("BEGIN")
            metadata = {key: json.loads(value) for key, value in db.execute("SELECT key,value FROM metadata")}
            if (metadata.get("status") != "completed_projection"
                    or metadata.get("built_at") != inputs["catalogue"]["builtAt"]
                    or metadata.get("sources") != inputs["sources"]):
                raise ValueError("The catalogue generation differs from the frozen music input")
            artifact = self._write_artists(db, inputs["catalogue"]["sha256"])
            expected = db.execute("SELECT count(*) FROM artists").fetchone()[0]
            if artifact["rows"] != expected:
                raise ValueError("Artist context did not cover every scoped catalogue artist")

        # Authenticate the actual opened snapshot and both parsed manifests at
        # publication. An atomic replacement must not relabel exported context.
        if (self._stamp(catalogue) != catalogue_stamp
                or sha256_file(catalogue) != inputs["catalogue"]["sha256"]):
            raise ValueError("The catalogue changed during artist context export")
        if (sha256_file(self.input_manifest) != self.input_sha
                or sha256_file(self.output_manifest) != self.output_sha):
            raise ValueError("A music manifest changed during artist context export")

        manifest = {"version": "composition-artist-context-v1", "state": "completed",
                    "musicInputManifest": input_artifact,
                    "musicOutputManifest": output_artifact,
                    "catalogue": inputs["catalogue"], "artists": artifact,
                    "sources": inputs["sources"], "namesAreAuthorshipEvidence": False,
                    "runtime": {"exporterVersion": "composition-artist-context-export-v1",
                                "pythonVersion": sys.version, "sourceSha256": sha256_file(__file__)}}
        pending = self.destination / "manifest.json.pending"
        pending.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        self._space()
        pending.rename(self.destination / "manifest.json")
        return manifest

    def _write_artists(self, db, catalogue_sha):
        path = self.destination / "artists.jsonl.gz"
        count = 0
        with gzip.open(path, "wt", encoding="utf-8") as stream:
            for row in db.execute("SELECT scope,uuid,name FROM artists ORDER BY scope,uuid"):
                value = {"scope": row["scope"], "artistUuid": row["uuid"], "name": row["name"],
                         "musicBrainzId": None,
                         "origin": {"kind": "derived_catalogue_cell", "table": "artists",
                                    "key": {"scope": row["scope"], "uuid": row["uuid"]},
                                    "field": "name", "catalogueSha256": catalogue_sha}}
                stream.write(json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n")
                count += 1
                if count % 100 == 0:
                    self._space()
        self._space()
        return {"path": path.name, "bytes": path.stat().st_size, "sha256": sha256_file(path), "rows": count}

    def _space(self):
        if shutil.disk_usage(self.destination).free < self.minimum_free_bytes:
            raise CatalogueBuildPaused("Artist context export stopped to preserve free space")
        if sum(path.stat().st_size for path in self.destination.iterdir() if path.is_file()) > self.maximum_bytes:
            raise CatalogueBuildPaused("Artist context export reached its byte cap")

    @staticmethod
    def _manifest(path, expected_sha):
        raw = path.read_bytes()
        if hashlib.sha256(raw).hexdigest() != expected_sha:
            raise ValueError("A music manifest differs from its supplied SHA256 pin")
        return json.loads(raw), {"path": str(path), "bytes": len(raw), "sha256": expected_sha}

    @staticmethod
    def _stamp(path):
        stat = path.stat()
        return stat.st_ino, stat.st_size, stat.st_mtime_ns


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--music-input-manifest", required=True)
    parser.add_argument("--music-input-sha256", required=True)
    parser.add_argument("--music-output-manifest", required=True)
    parser.add_argument("--music-output-sha256", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--minimum-free-bytes", type=int, default=int(5.5 * 1024**3))
    parser.add_argument("--maximum-bytes", type=int, default=16 * 1024**2)
    args = parser.parse_args()
    result = CompositionArtistContextExporter(args.music_input_manifest, args.music_output_manifest, args.output,
        input_manifest_sha256=args.music_input_sha256, output_manifest_sha256=args.music_output_sha256,
        minimum_free_bytes=args.minimum_free_bytes, maximum_bytes=args.maximum_bytes).export()
    print(json.dumps(result, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
