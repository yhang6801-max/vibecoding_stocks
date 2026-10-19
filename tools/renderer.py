"""Choose and build a renderer once, before starting concurrent chunks."""
from __future__ import annotations
import os
from pathlib import Path
import platform
import shutil

from media import powershell, run
from project import ROOT, digest, read_json, write_json

PORTABLE = ROOT / 'engines' / 'portable'


def dotnet_path():
    candidate = os.environ.get('DOTNET_BIN') or shutil.which('dotnet')
    if not candidate:
        local = Path.home() / '.local/share/stock-video/dotnet/dotnet'
        if local.is_file():
            candidate = str(local)
    if not candidate:
        raise FileNotFoundError('Skia 渲染需要 .NET 8 SDK；请安装 dotnet 或设置 DOTNET_BIN')
    return str(candidate)


def select_backend(requested='auto'):
    return ('windows' if os.name == 'nt' else 'skia') if requested == 'auto' else requested


def renderer_command(backend='auto'):
    backend = select_backend(backend)
    if backend == 'windows':
        if os.name != 'nt':
            raise RuntimeError('windows 后端仅适用于 Windows，请使用 --backend skia')
        return [powershell(), '-NoProfile', '-File']
    dotnet = dotnet_path()
    dll = PORTABLE / 'bin/Release/net8.0/StockVideo.Renderer.dll'
    stamp = PORTABLE / 'bin/inputs.json'
    sources = list((ROOT / 'engines').glob('*.cs')) + list(PORTABLE.glob('*.cs')) + list(PORTABLE.glob('*.csproj')) + list(PORTABLE.glob('packages.lock.json'))
    fingerprint = {str(p.relative_to(ROOT)): digest(p) for p in sources}
    fingerprint['platform'] = platform.platform()
    if not dll.is_file() or not stamp.is_file() or read_json(stamp) != fingerprint:
        run([dotnet, 'build', PORTABLE / 'StockVideo.Renderer.csproj', '-c', 'Release', '--nologo', '-p:RestoreLockedMode=true'])
        lock = PORTABLE / "packages.lock.json"
        if lock.is_file():
            fingerprint[str(lock.relative_to(ROOT))] = digest(lock)
        write_json(stamp, fingerprint)
    return [dotnet, str(dll)]


def identity(command):
    """Fingerprint runtime and artifact as well as source; used by resume manifests."""
    if len(command) == 2:
        return {'dll': digest(Path(command[1])), 'runtime': run([command[0], '--version'], capture_output=True, text=True).stdout.strip()}
    return {'powershell': run([command[0], '-NoProfile', '-Command', '$PSVersionTable.PSVersion.ToString()'], capture_output=True, text=True).stdout.strip()}
