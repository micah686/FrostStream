#!/usr/bin/env python3
"""Export the application images built and installed by release CI."""
import argparse
import gzip
import hashlib
from pathlib import Path
import subprocess
import yaml


def export(app, output, architecture):
    images = set()
    for mode in ('full', 'lite'):
        graph = yaml.safe_load((app / f'docker-compose-{mode}/docker-compose.yaml').read_text())
        images.update(service['image'] for service in graph['services'].values() if 'build' in service)
    for image in images:
        actual = subprocess.check_output(['docker', 'image', 'inspect', '--format', '{{.Architecture}}', image], text=True).strip()
        if actual != architecture:
            raise RuntimeError(f'{image}: expected {architecture}, found {actual}')
    output.mkdir(parents=True, exist_ok=True)
    archive = output / f'froststream-images-linux-{architecture}.tar.gz'
    # Compress the stream so all images share their base layers in a single archive.
    with gzip.open(archive, 'wb') as destination:
        process = subprocess.Popen(['docker', 'image', 'save', *sorted(images)], stdout=subprocess.PIPE)
        try:
            while block := process.stdout.read(1024 * 1024):
                destination.write(block)
            if process.wait() != 0:
                raise RuntimeError('Docker image export failed')
        finally:
            process.stdout.close()
            if process.poll() is None:
                process.kill()
                process.wait()
    checksum = hashlib.sha256()
    with archive.open('rb') as source:
        while block := source.read(1024 * 1024):
            checksum.update(block)
    with (output / 'SHA256SUMS').open('a') as manifest:
        manifest.write(checksum.hexdigest() + '  ' + archive.name + '\n')
    print(f'Exported {len(images)} verified application images for Linux {architecture}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--architecture', choices=('amd64', 'arm64'), required=True)
    args = parser.parse_args()
    export(Path(__file__).resolve().parent.parent, args.output.resolve(), args.architecture)
