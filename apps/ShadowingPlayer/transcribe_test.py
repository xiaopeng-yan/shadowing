import argparse
import os
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


def choose_device(device_arg, compute_type_arg):
    def cuda_runtime_available():
        path_entries = os.environ.get("PATH", "").split(os.pathsep)
        required_dlls = ("cublas64_12.dll", "cudnn64_9.dll")
        for dll_name in required_dlls:
            found = any(Path(path_entry, dll_name).exists() for path_entry in path_entries if path_entry)
            if not found:
                return False, dll_name
        return True, None

    if compute_type_arg:
        if device_arg == "auto":
            device, _ = choose_device(device_arg, None)
            return device, compute_type_arg
        return device_arg, compute_type_arg

    if device_arg != "auto":
        if device_arg == "cuda":
            available, missing_dll = cuda_runtime_available()
            if not available:
                print(
                    f"CUDA runtime missing {missing_dll}; falling back to CPU.",
                    file=sys.stderr,
                )
                return "cpu", "int8"
        return device_arg, "float16" if device_arg == "cuda" else "int8"

    cuda_path = os.environ.get("CUDA_PATH")
    cudnn_path = os.environ.get("CUDNN_PATH")
    cuda_bin = Path(cuda_path, "bin", "nvcc.exe") if cuda_path else None
    cudnn_bin = (
        Path(cudnn_path, "bin", "13.3", "x64", "cudnn64_9.dll")
        if cudnn_path
        else None
    )

    cuda_available, _ = cuda_runtime_available()
    if cuda_bin and cuda_bin.exists() and cudnn_bin and cudnn_bin.exists() and cuda_available:
        return "cuda", "float16"

    return "cpu", "int8"


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
    parser.add_argument(
        "--device",
        default="auto",
        choices=["auto", "cpu", "cuda"],
        help="Execution device. Use auto to prefer CUDA when available.",
    )
    parser.add_argument(
        "--compute-type",
        default=None,
        help="Override the faster-whisper compute type. Defaults to float16 on CUDA, int8 on CPU.",
    )
    args = parser.parse_args()

    media_path = Path(args.media)
    if not media_path.exists():
        raise SystemExit(f"File not found: {media_path}")

    device, compute_type = choose_device(args.device, args.compute_type)
    model = WhisperModel(args.model, device=device, compute_type=compute_type)
    segments, info = model.transcribe(
        str(media_path),
        language=args.language,
        vad_filter=True,
    )
    segments = list(segments)

    if args.srt:
        write_srt(args.srt, segments)

    print(f"Using device: {device} ({compute_type})")
    print(f"Detected language: {info.language} ({info.language_probability:.2%})")
    print()

    for segment in segments:
        start = format_timestamp(segment.start)
        end = format_timestamp(segment.end)
        print(f"[{start} -> {end}] {segment.text.strip()}")


if __name__ == "__main__":
    main()
