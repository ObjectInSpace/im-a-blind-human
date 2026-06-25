import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from image_tools.ollama import OllamaClient, OllamaError


class _Response:
    def __init__(self, body: dict[str, object]) -> None:
        self.body = json.dumps(body).encode()

    def __enter__(self):
        return self

    def __exit__(self, *_args):
        return None

    def read(self) -> bytes:
        return self.body


class OllamaClientTests(unittest.TestCase):
    def test_describe_sends_image_and_returns_trimmed_text(self):
        with tempfile.TemporaryDirectory() as temp:
            image = Path(temp) / "image.png"
            image.write_bytes(b"png")
            with patch("urllib.request.urlopen", return_value=_Response({"response": "  Visible detail.  "})) as open_mock:
                result = OllamaClient(model="vision-test", num_gpu=0).describe(image, "Prompt")
            self.assertEqual("Visible detail.", result)
            payload = json.loads(open_mock.call_args.args[0].data)
            self.assertEqual("vision-test", payload["model"])
            self.assertEqual(["cG5n"], payload["images"])
            self.assertEqual(0, payload["options"]["num_gpu"])
            self.assertFalse(payload["think"])
            self.assertEqual(128, payload["options"]["num_predict"])

    def test_empty_response_is_an_error(self):
        with tempfile.TemporaryDirectory() as temp:
            image = Path(temp) / "image.png"
            image.write_bytes(b"png")
            with patch("urllib.request.urlopen", return_value=_Response({"response": ""})):
                with self.assertRaises(OllamaError):
                    OllamaClient().describe(image, "Prompt")


if __name__ == "__main__":
    unittest.main()
