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
DURATION = 0.290
PEAK = 0.135  # Approximately -17.4 dBFS, quieter than the other built-in cues.


def soft_note(time, frequency, level, decay):
    if not 0.0 < time < 0.250:
        return 0.0
    # Rounded attack and a long taper avoid a sharp strike or an abrupt cutoff.
    attack = math.sin(min(1.0, time / 0.018) * math.pi / 2) ** 2
    release = math.cos(max(0.0, (time - 0.180) / 0.070) * math.pi / 2) ** 2
    phase = 2 * math.pi * frequency * time
    body = math.sin(phase) + 0.08 * math.exp(-time / 0.035) * math.sin(2 * phase)
    return level * attack * math.exp(-time / decay) * release * body


def synthesize():
    """A warm, felt-like touch: two overlapping mellow notes with no noise layer."""
    samples = []
    for index in range(round(SAMPLE_RATE * DURATION)):
        time = index / SAMPLE_RATE
        # F4 and a quieter C5 form one soft gesture rather than two alert beeps.
        samples.append(soft_note(time - 0.008, 349.228, 1.0, 0.075)
                       + soft_note(time - 0.022, 523.251, 0.28, 0.065))

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
