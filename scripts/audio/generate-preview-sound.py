"""Synthesize CyberSnap's capture-preview cue using Python's standard library and FFmpeg."""

import argparse
import math
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile
import wave


SAMPLE_RATE = 44_100
DURATION = 0.210
PEAK = 0.24
# Keep the approved Option C level: +1.4 dB after peak normalization.
# This matches the system cue's measured loudness in the audition comparison.
MATCHED_GAIN = 10 ** (1.4 / 20)


def soft_note(time, frequency, duration, attack, decay):
    if not 0.0 < time < duration:
        return 0.0
    onset = math.sin(min(1.0, time / attack) * math.pi / 2) ** 2
    release = math.sin(min(1.0, (duration - time) / 0.025) * math.pi / 2) ** 2
    phase = 2 * math.pi * frequency * time
    return onset * math.exp(-time / decay) * release * math.sin(phase)


def synthesize():
    """The approved Option C: two brief, rounded, descending taps."""
    samples = []
    for index in range(round(SAMPLE_RATE * DURATION)):
        time = index / SAMPLE_RATE
        samples.append(0.62 * soft_note(time - 0.008, 830.609, 0.080, 0.005, 0.023)
                       + soft_note(time - 0.067, 659.255, 0.110, 0.007, 0.032))

    gain = PEAK / max(abs(value) for value in samples)
    return b''.join(struct.pack('<h', round(value * gain * MATCHED_GAIN * 32767)) for value in samples)


def main():
    default_output = Path(__file__).resolve().parents[2] / 'src/CyberSnap/Assets/Sounds/preview.mp3'
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=default_output)
    parser.add_argument('--ffmpeg', default=shutil.which('ffmpeg'))
    args = parser.parse_args()
    if not args.ffmpeg:
        parser.error('FFmpeg is required to encode the generated PCM as MP3.')
    if not args.output.parent.is_dir():
        parser.error('The output directory must already exist.')

    # Encode off to the side so an FFmpeg failure preserves the existing asset.
    temporary_files = []
    try:
        for suffix in ('.wav', '.mp3'):
            with tempfile.NamedTemporaryFile(prefix='cybersnap-preview-', suffix=suffix, delete=False) as file:
                temporary_files.append(Path(file.name))
        wav_path, mp3_path = temporary_files
        with wave.open(str(wav_path), 'wb') as writer:
            writer.setnchannels(1)
            writer.setsampwidth(2)
            writer.setframerate(SAMPLE_RATE)
            writer.writeframes(synthesize())
        subprocess.run([
            args.ffmpeg, '-hide_banner', '-loglevel', 'error', '-y', '-i', str(wav_path),
            '-map_metadata', '-1', '-codec:a', 'libmp3lame', '-b:a', '192k',
            '-ar', str(SAMPLE_RATE), '-ac', '1', str(mp3_path),
        ], check=True)
        shutil.copyfile(mp3_path, args.output)
        print(f'Generated {args.output}: {DURATION * 1000:.0f} ms, mono {SAMPLE_RATE} Hz, 192 kbps MP3.')
    finally:
        for path in temporary_files:
            path.unlink(missing_ok=True)


if __name__ == '__main__':
    main()
