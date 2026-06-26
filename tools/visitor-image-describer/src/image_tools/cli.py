"""Command-line interface for image-tools."""

from __future__ import annotations

import argparse
from pathlib import Path

from .ollama import DEFAULT_MODEL, OllamaClient
from .pipeline import build_manifest, describe_manifest


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="image-tools", description="Prepare and describe images with local Ollama")
    sub = parser.add_subparsers(dest="command", required=True)

    prepare = sub.add_parser("prepare", help="Build a prepared-image manifest from the recovered sign mapping")
    prepare.add_argument("--mapping", type=Path, required=True)
    prepare.add_argument("--textures", type=Path, required=True)
    prepare.add_argument("--sprites", type=Path, help="Sliced Sprite asset dir; enables best-frame crop for animations")
    prepare.add_argument("--prepared-dir", type=Path, required=True)
    prepare.add_argument("--output", type=Path, required=True)

    describe = sub.add_parser("describe", help="Describe prepared images and write resumable JSON/CSV catalogs")
    describe.add_argument("--manifest", type=Path, required=True)
    describe.add_argument("--json", type=Path, required=True)
    describe.add_argument("--csv", type=Path, required=True)
    describe.add_argument("--model", default=DEFAULT_MODEL)
    describe.add_argument("--url")
    describe.add_argument("--timeout", type=float, default=300)
    describe.add_argument("--num-gpu", type=int, help="Layers to offload to the GPU; use 0 to force CPU inference")
    describe.add_argument("--num-ctx", type=int, help="Model context size; lower this to reduce memory use")
    describe.add_argument("--num-batch", type=int, help="Prompt batch size; lower this to reduce compute memory")
    describe.add_argument("--think", action="store_true", help="Enable model reasoning (normally unnecessary for descriptions)")
    describe.add_argument("--num-predict", type=int, default=128, help="Maximum generated tokens per image")
    describe.add_argument("--limit", type=int)
    describe.add_argument("--per-sign", type=int, help="Process at most this many images from each sign category")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    if args.command == "prepare":
        tasks = build_manifest(args.mapping, args.textures, args.prepared_dir, args.output, args.sprites)
        print(f"Prepared {len(tasks)} description tasks in {args.output}")
        return 0
    if args.command == "describe":
        client = OllamaClient(
            url=args.url,
            model=args.model,
            timeout=args.timeout,
            num_gpu=args.num_gpu,
            num_ctx=args.num_ctx,
            num_batch=args.num_batch,
            think=args.think,
            num_predict=args.num_predict,
        )
        results = describe_manifest(args.manifest, args.json, args.csv, client, args.limit, args.per_sign)
        print(f"Catalog contains {len(results)} descriptions in {args.json} and {args.csv}")
        return 0
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
