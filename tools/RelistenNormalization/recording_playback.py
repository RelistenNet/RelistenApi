"""Read exact catalog recording files without moving their playback ownership.

Directory pages supply a source UUID, never a destination URL. The configured
immutable-result reader owns file membership and audio locators; this adapter
only presents its bounded results in the listener's current discovery page.
"""

import json
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode, urlsplit
from urllib.request import urlopen
from uuid import UUID


def reader_url(value):
    """Validate optional operator-configured links; absent readers stay hidden."""
    if not value:
        return None
    parsed = urlsplit(value)
    if parsed.scheme not in {"http", "https"} or not parsed.hostname or parsed.username or parsed.password:
        raise ValueError("Reader URLs must use HTTP or HTTPS without embedded credentials")
    if parsed.query or parsed.fragment:
        raise ValueError("Configure a reader root without a query or fragment")
    return value.rstrip("/") + "/"


def reader_navigation(music_url=None, people_url=None, venue_url=None, own=None):
    music = reader_url(music_url)
    navigation = {key: value for key, value in {
        "songs": music + "#songs" if music else None,
        "compositions": music + "#compositions" if music else None,
        "venues": reader_url(venue_url), "tapers": reader_url(people_url),
    }.items() if value}
    if own:
        navigation[own] = "/"
    return navigation


class RecordingPlayback:
    MAXIMUM_RESPONSE_BYTES = 8 * 1024**2
    PAGE_SIZE = 30

    def __init__(self, files_url):
        self.files_url = reader_url(files_url)

    @staticmethod
    def source_uuid(parameters):
        return str(UUID(parameters.get("source_uuid", "")))

    def _read(self, path, parameters):
        if not self.files_url:
            raise ValueError("No recording file reader is configured")
        try:
            with urlopen(self.files_url + path + "?" + urlencode(parameters), timeout=20) as response:
                body = response.read(self.MAXIMUM_RESPONSE_BYTES + 1)
            if len(body) > self.MAXIMUM_RESPONSE_BYTES:
                raise ValueError("The file reader response is too large. Open its source evidence instead.")
            return json.loads(body)
        except (HTTPError, URLError, TimeoutError, OSError, json.JSONDecodeError) as error:
            if isinstance(error, HTTPError):
                error.close()
            raise ValueError("The recording's file reader is unavailable. Try again or open its source evidence.") from error

    def files(self, parameters):
        source = self.source_uuid(parameters)
        offset = max(0, int(parameters.get("offset", 0)))
        result = self._read("api/records", dict(domain="file_contents", entityKey="source:" + source,
                                                offset=offset, limit=self.PAGE_SIZE))
        if len(result["items"]) > self.PAGE_SIZE or result["limit"] > self.PAGE_SIZE:
            raise ValueError("The file reader exceeded the requested track page size")
        items = []
        for item in result["items"]:
            if item.get("domain") != "file_contents" or "source:" + source not in item.get("entityKeys", []):
                raise ValueError("The file reader returned a different recording. Reload this page.")
            items.append(dict(id=item["id"], title=item.get("label") or "Untitled file"))
        return dict(sourceUuid=source, items=items, total=result["total"], offset=result["offset"], limit=result["limit"])

    def file(self, parameters):
        source = self.source_uuid(parameters)
        identifier = int(parameters.get("id", ""))
        if identifier < 1:
            raise ValueError("Choose an original recording file")
        detail = self._read("api/record", dict(id=identifier))
        record = detail["record"]
        proposal = record.get("proposal") or {}
        home = proposal.get("home") or {}
        if record.get("domain") != "file_contents" or home.get("sourceUuid") != source:
            raise ValueError("This file does not belong to the selected recording. Reload its tracks.")
        # A native parent can contain many children. Its recursive playback list
        # cannot be presented as one track or used to invent a child assignment.
        urls = [] if isinstance(proposal.get("files"), list) else detail.get("playback", [])
        playback = []
        for value in urls:
            parsed = urlsplit(value)
            if parsed.scheme in {"http", "https"} and parsed.hostname and not parsed.username and not parsed.password:
                if value not in playback:
                    playback.append(value)
        return dict(sourceUuid=source, fileUuid=home.get("fileUuid"), showUuid=home.get("showUuid"),
                    title=(proposal.get("titleInterpretation") or {}).get("originalTitle"), playback=playback)
