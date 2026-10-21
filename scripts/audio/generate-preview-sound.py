"""Synthesize CyberSnap's capture-preview cue using Python's standard library and FFmpeg."""

import argparse
import math
from pathlib import Path
import random
import shutil
import struct
import subprocess
import tempfile
import wave


SAMPLE_RATE = 44_100
DURATION = 0.265
PEAK = 0.24  # Match the other built-in UI cues, approximately -12.4 dBFS.


def synthesize():
    """A short, airy card slide followed by a soft, dry settling tap."""
    rng = random.Random(0x43534E50)
    samples = []
    x1 = x2 = y1 = y2 = 0.0
    tap_phase = 0.0
    for index in range(round(SAMPLE_RATE * DURATION)):
        time = index / SAMPLE_RATE
        sample = 0.0

        # A broad, upward-moving band of air, distinct from the other pitched blips.
        progress = (time - 0.008) / 0.187
        if 0.0 <= progress < 1.0:
            center = 950 + 2_650 * progress ** 0.8
            omega = 2 * math.pi * center / SAMPLE_RATE
            alpha = math.sin(omega) / (2 * 0.7)
            b0 = alpha / (1 + alpha)
            a1 = -2 * math.cos(omega) / (1 + alpha)
            a2 = (1 - alpha) / (1 + alpha)
            noise = rng.uniform(-1, 1)
            filtered = b0 * noise - b0 * x2 - a1 * y1 - a2 * y2
            x2, x1 = x1, noise
            y2, y1 = y1, filtered
            envelope = math.sin(math.pi * progress) ** 1.5 * math.exp(-2.2 * progress)
            sample += filtered * envelope

        # An inharmonic, heavily damped tap closes the gesture without a ringing note.
        tap_time = time - 0.157
        if 0 <= tap_time < 0.055:
            tap_phase += 2 * math.pi * (460 * math.exp(-tap_time * 18)) / SAMPLE_RATE
            attack = 1 - math.exp(-tap_time / 0.0015)
            decay = math.exp(-tap_time / 0.009)
            release = min(1.0, (0.055 - tap_time) / 0.005)
            body = math.sin(tap_phase) + 0.25 * math.sin(tap_phase * 2.37)
            sample += 0.24 * attack * decay * release * body

        samples.append(sample)

    # Taper and remove DC before peak normalization to avoid abrupt waveform edges.
    offset = sum(samples) / len(samples)
    for index, value in enumerate(samples):
        edge = min(1.0, index / (SAMPLE_RATE * 0.005), (len(samples) - 1 - index) / (SAMPLE_RATE * 0.005))
        samples[index] = (value - offset) * edge
    gain = PEAK / max(abs(value) for value in samples)
    return b''.join(struct.pack('<h', round(value * gain * 32767)) for value in samples)


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
