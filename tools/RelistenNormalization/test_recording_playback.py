"""Listener track browsing through both directories' public HTTP boundaries."""

from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import threading
import unittest
from urllib.error import HTTPError
from urllib.parse import parse_qs, urlencode, urlsplit
from urllib.request import urlopen

from people_directory import handler_for as people_handler
from recording_playback import RecordingPlayback, reader_navigation
from venue_directory import handler_for as venue_handler


SOURCE = "00000000-0000-0000-0000-000000000001"
OTHER_SOURCE = "00000000-0000-0000-0000-000000000002"


@contextmanager
def server(handler):
    instance = ThreadingHTTPServer(("127.0.0.1", 0), handler)
    thread = threading.Thread(target=instance.serve_forever, daemon=True)
    thread.start()
    try:
        yield "http://127.0.0.1:" + str(instance.server_port) + "/"
    finally:
        instance.shutdown()
        instance.server_close()
        thread.join()


class TestRecordingPlayback(unittest.TestCase):
    def setUp(self):
        self.queries = []
        self.owner = SOURCE
        self.audio = ["https://archive.org/download/recording/song.mp3", "javascript:alert(1)"]
        self.proposal_files = None
        self.failure_status = None
        test = self

        class Reader(BaseHTTPRequestHandler):
            def do_GET(self):
                route = urlsplit(self.path)
                query = {key: values[-1] for key, values in parse_qs(route.query).items()}
                test.queries.append((route.path, query))
                if test.failure_status:
                    self.send_response(test.failure_status)
                    self.end_headers()
                    return
                if route.path == "/api/records":
                    result = dict(items=[dict(id=38, label="Scarlet Begonias", domain="file_contents", entityKeys=["source:" + SOURCE])],
                                  total=45, offset=int(query["offset"]), limit=int(query["limit"]))
                else:
                    proposal = dict(home=dict(sourceUuid=test.owner, showUuid="late-show", fileUuid="original-file"),
                                    titleInterpretation=dict(originalTitle="Scarlet Begonias"))
                    if test.proposal_files is not None:
                        proposal["files"] = test.proposal_files
                    result = dict(record=dict(domain="file_contents", proposal=proposal), playback=test.audio)
                body = json.dumps(result).encode()
                self.send_response(200)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, *args):
                pass

        self.reader_handler = Reader

    @staticmethod
    def get(origin, path, **query):
        with urlopen(origin + path + "?" + urlencode(query)) as response:
            return json.load(response)

    def test_both_directories_browse_and_play_exact_recording_without_changing_its_home(self):
        with server(self.reader_handler) as origin:
            directory = type("Directory", (), {"playback": RecordingPlayback(origin)})()
            for handler in (people_handler, venue_handler):
                with self.subTest(directory=handler.__module__), server(handler(directory)) as public:
                    tracks = self.get(public, "api/recording-files", source_uuid=SOURCE, offset=30)
                    self.assertEqual(tracks["items"], [dict(id=38, title="Scarlet Begonias")])
                    self.assertEqual((tracks["total"], tracks["offset"], tracks["limit"]), (45, 30, 30))
                    detail = self.get(public, "api/recording-file", source_uuid=SOURCE, id=38)
                    self.assertEqual(detail["playback"], [self.audio[0]])
                    self.assertEqual((detail["sourceUuid"], detail["showUuid"], detail["fileUuid"]), (SOURCE, "late-show", "original-file"))
        self.assertTrue(all(query.get("entityKey") == "source:" + SOURCE for path, query in self.queries if path == "/api/records"))

    def test_file_from_another_recording_is_rejected_instead_of_played(self):
        self.owner = OTHER_SOURCE
        with server(self.reader_handler) as origin, server(people_handler(type("Directory", (), {"playback": RecordingPlayback(origin)})())) as public:
            with self.assertRaises(HTTPError) as error:
                self.get(public, "api/recording-file", source_uuid=SOURCE, id=38)
            self.assertEqual(error.exception.code, 400)
            with error.exception:
                self.assertIn("does not belong", json.load(error.exception)["error"])

    def test_native_parent_does_not_publish_its_children_as_one_track(self):
        self.proposal_files = [{"file": {"mp3Url": self.audio[0]}}]
        with server(self.reader_handler) as origin:
            result = RecordingPlayback(origin).file(dict(source_uuid=SOURCE, id=38))
        self.assertEqual(result["playback"], [])

    def test_invalid_source_does_not_contact_reader(self):
        with server(self.reader_handler) as origin:
            with self.assertRaises(ValueError):
                RecordingPlayback(origin).files(dict(source_uuid="https://other-host/"))
        self.assertEqual(self.queries, [])

    def test_reader_outage_preserves_recording_and_allows_an_explicit_retry(self):
        with server(self.reader_handler) as origin:
            reader = RecordingPlayback(origin)
            self.failure_status = 503
            with self.assertRaisesRegex(ValueError, "file reader is unavailable"):
                reader.files(dict(source_uuid=SOURCE))
            self.failure_status = None
            self.assertEqual(reader.files(dict(source_uuid=SOURCE))["sourceUuid"], SOURCE)

    def test_optional_navigation_has_no_invented_reader_locations(self):
        self.assertEqual(reader_navigation(own="tapers"), {"tapers": "/"})
        self.assertEqual(reader_navigation(music_url="https://music.example", venue_url="https://venue.example", own="tapers"),
                         dict(songs="https://music.example/#songs", compositions="https://music.example/#compositions",
                              venues="https://venue.example/", tapers="/"))
        with self.assertRaises(ValueError):
            reader_navigation(music_url="javascript:alert(1)")


if __name__ == "__main__":
    unittest.main()
