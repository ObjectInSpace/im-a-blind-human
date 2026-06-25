"""Image description through a local Ollama server."""

from __future__ import annotations

import base64
import json
import os
import urllib.error
import urllib.request
from pathlib import Path
from typing import Sequence

DEFAULT_URL = "http://localhost:11434/api/generate"
DEFAULT_MODEL = "qwen3-vl:4b-instruct"


class OllamaError(RuntimeError):
    """Raised when Ollama cannot return a usable response."""


class OllamaClient:
    def __init__(
        self,
        url: str | None = None,
        model: str = DEFAULT_MODEL,
        timeout: float = 300,
        num_gpu: int | None = None,
        num_ctx: int | None = None,
        num_batch: int | None = None,
        think: bool = False,
        num_predict: int = 128,
    ) -> None:
        self.url = url or os.environ.get("OLLAMA_URL", DEFAULT_URL)
        self.model = model
        self.timeout = timeout
        self.num_gpu = num_gpu
        self.num_ctx = num_ctx
        self.num_batch = num_batch
        self.think = think
        self.num_predict = num_predict

    def describe(
        self,
        image_paths: str | Path | Sequence[str | Path],
        prompt: str,
    ) -> str:
        paths = [Path(image_paths)] if isinstance(image_paths, (str, Path)) else [Path(p) for p in image_paths]
        if not paths:
            raise ValueError("At least one image is required")
        for path in paths:
            if not path.is_file():
                raise FileNotFoundError(f"Image not found: {path}")

        options: dict[str, int] = {"temperature": 0, "num_predict": self.num_predict}
        if self.num_gpu is not None:
            options["num_gpu"] = self.num_gpu
        if self.num_ctx is not None:
            options["num_ctx"] = self.num_ctx
        if self.num_batch is not None:
            options["num_batch"] = self.num_batch
        payload = json.dumps(
            {
                "model": self.model,
                "prompt": prompt,
                "images": [base64.b64encode(path.read_bytes()).decode("ascii") for path in paths],
                "stream": False,
                "think": self.think,
                "options": options,
            }
        ).encode("utf-8")
        request = urllib.request.Request(
            self.url,
            data=payload,
            headers={"Content-Type": "application/json"},
            method="POST",
        )

        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                body = json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode("utf-8", errors="replace")
            raise OllamaError(f"Ollama returned HTTP {exc.code}: {detail}") from exc
        except urllib.error.URLError as exc:
            raise OllamaError(f"Could not reach Ollama at {self.url}: {exc.reason}") from exc
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise OllamaError("Ollama returned an invalid JSON response") from exc

        if body.get("error"):
            raise OllamaError(str(body["error"]))
        text = str(body.get("response", "")).strip()
        if not text:
            raise OllamaError("Ollama returned an empty description")
        return text


def describe_image(
    image_path: str | Path,
    prompt: str = "Describe the image.",
    model: str = DEFAULT_MODEL,
) -> str:
    """Compatibility wrapper matching the original gist."""

    return OllamaClient(model=model).describe(image_path, prompt)
