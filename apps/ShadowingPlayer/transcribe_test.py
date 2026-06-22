import argparse
import sys
from pathlib import Path

from faster_whisper import WhisperModel

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8")


def format_timestamp(seconds):
    total_ms = int(round(seconds * 1000))
    ms = total_ms % 1000
    total_seconds = total_ms // 1000
    s = total_seconds % 60
    total_minutes = total_seconds // 60
    m = total_minutes % 60
    h = total_minutes // 60
    return f"{h:02d}:{m:02d}:{s:02d}.{ms:03d}"


def format_srt_timestamp(seconds):
    return format_timestamp(seconds).replace(".", ",")


def write_srt(path, segments):
    with open(path, "w", encoding="utf-8") as srt:
        for index, segment in enumerate(segments, start=1):
            srt.write(f"{index}\n")
            srt.write(
                f"{format_srt_timestamp(segment.start)} --> "
                f"{format_srt_timestamp(segment.end)}\n"
            )
            srt.write(segment.text.strip() + "\n\n")


def main():
    parser = argparse.ArgumentParser(description="faster-whisper transcription helper.")
    parser.add_argument("media", help="Path to a video or audio file.")
    parser.add_argument(
        "--model",
        default="small",
        help="Whisper model size/name. Try tiny, base, small, medium, or large-v3.",
    )
    parser.add_argument(
        "--language",
        default=None,
        help="Optional language code, for example en, de, zh, ja. Omit to auto-detect.",
    )
    parser.add_argument(
        "--srt",
        default=None,
        help="Optional path to write an SRT subtitle file.",
    )
    args = parser.parse_args()

    media_path = Path(args.media)
    if not media_path.exists():
        raise SystemExit(f"File not found: {media_path}")

    model = WhisperModel(args.model, device="cpu", compute_type="int8")
    segments, info = model.transcribe(
        str(media_path),
        language=args.language,
        vad_filter=True,
    )
    segments = list(segments)

    if args.srt:
        write_srt(args.srt, segments)

    print(f"Detected language: {info.language} ({info.language_probability:.2%})")
    print()

    for segment in segments:
        start = format_timestamp(segment.start)
        end = format_timestamp(segment.end)
        print(f"[{start} -> {end}] {segment.text.strip()}")


if __name__ == "__main__":
    main()
