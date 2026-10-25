"""Pacman repository databases, and which official packages a recipe needs
that the installer image does not carry.

A recipe's packages depend on packages from Omarchy's and Arch Linux ARM's
own repositories, and the image's offline mirror has only what every
install needs. The rest has to come along on the stick: this works out the
closure of the recipe's dependencies, leaves out what the offline mirror
and the recipe's repository have, and names the official packages to fetch,
with the repository each comes from. The Windows app does the same in
OfficialPackages.cs.
"""

import gzip
import io
import lzma
import re
import subprocess
import tarfile
from dataclasses import dataclass, field

ARCH = "aarch64"
# Where the official packages come from, in the order pacman.conf has them
# on an installed Omarchy; the first repository that has a name wins.
OFFICIAL_REPOS = [
    ("omarchy", f"https://pkgs.omarchy.org/edge/{ARCH}"),
    ("core", f"https://arm-mirror.omarchy.org/{ARCH}/core"),
    ("extra", f"https://arm-mirror.omarchy.org/{ARCH}/extra"),
    ("alarm", f"https://arm-mirror.omarchy.org/{ARCH}/alarm"),
]
MULTI = {"DEPENDS", "PROVIDES", "OPTDEPENDS", "MAKEDEPENDS", "CHECKDEPENDS", "CONFLICTS", "REPLACES", "LICENSE", "GROUPS"}


@dataclass
class Package:
    name: str
    version: str
    filename: str
    sha256: str
    size: int
    depends: list[str] = field(default_factory=list)
    provides: list[str] = field(default_factory=list)


def decompress(data: bytes) -> bytes:
    """A repository database's tar: repo-add writes gzip, Omarchy's zstd."""
    if data[:2] == b"\x1f\x8b":
        return gzip.decompress(data)
    if data[:4] == b"\x28\xb5\x2f\xfd":
        try:
            from compression import zstd  # Python 3.14
            return zstd.decompress(data)
        except ImportError:
            return subprocess.run(["zstd", "-dc"], input=data, capture_output=True, check=True).stdout
    if data[:6] == b"\xfd7zXZ\x00":
        return lzma.decompress(data)
    return data


def read_db(data: bytes) -> dict[str, Package]:
    """The packages in a repository database, by name. A database is a
    compressed tar with a directory per package: `desc` holds everything in
    repo-add's output, while Arch Linux ARM's databases still keep %DEPENDS%
    and %PROVIDES% in a separate `depends` file; both are read."""
    entries: dict[str, dict[str, list[str]]] = {}
    with tarfile.open(fileobj=io.BytesIO(decompress(data))) as tar:
        for member in tar.getmembers():
            folder, _, file = member.name.rpartition("/")
            if file not in ("desc", "depends") or not folder:
                continue
            fields = entries.setdefault(folder, {})
            key = None
            for line in tar.extractfile(member).read().decode().splitlines():
                if line.startswith("%") and line.endswith("%"):
                    key = line.strip("%")
                    fields.setdefault(key, [])
                elif key and line:
                    fields[key].append(line)
    packages = {}
    for fields in entries.values():
        one = lambda k: fields.get(k, [""])[0]  # noqa: E731
        if not one("NAME") or not one("FILENAME"):
            continue
        packages[one("NAME")] = Package(one("NAME"), one("VERSION"), one("FILENAME"), one("SHA256SUM").lower(),
                                        int(one("CSIZE") or 0), fields.get("DEPENDS", []), fields.get("PROVIDES", []))
    return packages


def bare(dep: str) -> str:
    """A dependency's name without its version constraint: libssc>=0.4 -> libssc."""
    return re.split(r"[<>=]", dep, 1)[0]


def provided(dbs: list[dict[str, Package]]) -> set[str]:
    """Every name these databases satisfy: package names and what they provide."""
    names = set()
    for db in dbs:
        for p in db.values():
            names.add(p.name)
            names.update(bare(v) for v in p.provides)
    return names


def resolve_official(wanted: list[str], recipe_db: dict[str, Package], offline_db: dict[str, Package],
                     official: list[tuple[str, str, dict[str, Package]]]) -> list[tuple[str, str, Package]]:
    """The official packages the stick must carry: (repo, server, package) for
    everything the recipe's packages need, directly or not, that neither the
    offline mirror nor the recipe's repository has. `official` is
    [(repo, server, db), ...] in order of preference. A dependency nothing
    provides raises LookupError naming it."""
    have = provided([offline_db, recipe_db])
    chosen: dict[str, tuple[str, str, Package]] = {}
    seen = set()
    queue = list(wanted)
    while queue:
        name = bare(queue.pop(0))
        if name in seen:
            continue
        seen.add(name)
        if name in recipe_db:
            queue.extend(recipe_db[name].depends)
            continue
        if name in have or name in chosen:
            continue
        for repo, server, db in official:
            package = db.get(name) or next((p for p in db.values() if name in (bare(v) for v in p.provides)), None)
            if package is not None:
                break
        else:
            raise LookupError(name)
        if package.name in chosen:
            continue
        chosen[package.name] = (repo, server, package)
        have.update(bare(v) for v in package.provides)
        queue.extend(package.depends)
    return sorted(chosen.values(), key=lambda c: c[2].name)
