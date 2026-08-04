from __future__ import annotations

import hashlib
import hmac
import io
import json
import unittest
from unittest.mock import patch

from scblctl.config import ServerConfig
from scblctl.live_state import LiveStateError, read_live_state


class FakeResponse(io.BytesIO):
    def __enter__(self):
        return self

    def __exit__(self, *_args):
        self.close()


class LiveStateTests(unittest.TestCase):
    def setUp(self) -> None:
        self.config = ServerConfig.new(public_host="sc6.example.com")
        self.config.network.secret = "test-secret"

    def test_signed_overlay_query_returns_current_players(self) -> None:
        payload = json.dumps(
            {"onlineCount": 2, "peers": [{"username": "A"}, {"username": "B"}]}
        ).encode()

        def open_request(request, timeout):
            self.assertEqual(5, timeout)
            timestamp = request.headers["X-scbl-timestamp"]
            expected = hmac.new(
                b"test-secret",
                f"{timestamp}\nGET\n/v1/peers\n".encode(),
                hashlib.sha256,
            ).hexdigest()
            self.assertEqual(expected, request.headers["X-scbl-signature"])
            return FakeResponse(payload)

        with patch("urllib.request.urlopen", side_effect=open_request):
            state = read_live_state(self.config)
        self.assertEqual(2, state.online_count)
        self.assertEqual(("A", "B"), state.usernames)

    def test_inconsistent_count_is_rejected(self) -> None:
        payload = json.dumps({"onlineCount": 2, "peers": []}).encode()
        with patch("urllib.request.urlopen", return_value=FakeResponse(payload)):
            with self.assertRaisesRegex(LiveStateError, "不一致"):
                read_live_state(self.config)


if __name__ == "__main__":
    unittest.main()
