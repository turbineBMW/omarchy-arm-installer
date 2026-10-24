# Recipes

## What they are

Omarchy runs the same way on every laptop it supports. Some laptops need more than Omarchy carries yet: a kernel with fixes still on their way upstream, audio tuning, plugins for hardware only that model has. People who work on a model publish those as their own pacman repository. A **recipe** is this installer's pointer to one: which models it is for, where the repository is, and which key signs it.

When the installer recognises a model with a recipe, it offers the repository's packages as **extras**, and installs them with Omarchy if the person agrees.

**Extras are not part of Omarchy.** The Omarchy team has not built or reviewed them. They come from the recipe's maintainer, they can replace Omarchy's own packages (a recipe's kernel replaces `linux-omarchy`), and they keep updating from the maintainer's repository until they are removed. The installer says all of this before anyone opts in, and lists every package and what it does.

## The pieces

| Where | What | Who writes it |
|---|---|---|
| This repository, `recipes/<id>/recipe.json` | Which models (CHIDs), the repository's address and key fingerprint, which packages to install, firmware the model needs | Added by PR and reviewed here |
| This repository, `recipes/<id>/key.asc` | The repository's public signing key | Same PR |
| The recipe's repository, `<repo>.recipe.json` + `.sig` | What the repository says about itself: each package, what it does, whether it replaces Omarchy's, where its fix is going upstream, and whether the repository is still needed | The maintainer, signed with the repository key |
| The recipe's repository, `<repo>.db` + `.sig`, `*.pkg.tar.*` + `.sig` | The pacman repository itself, every file signed | The maintainer |

### recipe.json

```json
{
  "id": "microsoft-surface-pro-11",
  "name": "Surface Pro 11 extras",
  "device": "Microsoft Surface Pro, 11th Edition (Snapdragon X Elite, OLED)",
  "status": "tested",
  "maintainer": { "name": "Brandon Williams", "github": "turbineBMW" },
  "match": { "chids": ["95971fb3-d478-591f-9ea3-eb0af0d1dfb5"] },
  "repo": {
    "name": "surface-pro-11",
    "server": "https://github.com/turbineBMW/surface-pro-11-omarchy/releases/download/$arch",
    "keyFingerprint": "6CAC3F8AE94AC7BDCFB7A9017C8D01DD008051AB"
  },
  "packages": ["omarchy-surface-pro-11", "surface-pro-11-plugins"],
  "firmware": [{ "folder": "qcdx8380.inf_arm64_*", "files": ["qcdxkmsuc8380.mbn"] }],
  "secureBoot": "How to turn Secure Boot off on this model.",
  "bootFromUsb": "How to start this model from USB."
}
```

| Field | |
|---|---|
| `match.chids` | Which machines get the offer. Pick the narrowest CHID that covers the model and no other, usually type 4 (manufacturer, family, product, SKU). `systemd-analyze chid` on Linux, or the hardware report, lists them. |
| `status` | `tested` once someone has installed with it on that model; `untested` says so in the installer. |
| `repo` | A pacman repository served over HTTPS, with `$arch` expanded to `aarch64`. Its name must not be one of Omarchy's or Arch's. |
| `packages` | What to install. Every package in the repository goes on the stick, and installed packages the repository also has (a kernel) are replaced from it. |
| `firmware` | Driver-store files the model can't do without, beyond what the installer finds on its own: every non-driver file in the packages of the laptop's devices. The installer stops if one is missing. |
| `secureBoot`, `bootFromUsb` | The last screen's instructions for this model, instead of the general ones. |

### The repository's description

Published beside the database as `<repo>.recipe.json`, with a detached signature `<repo>.recipe.json.sig` by the repository key:

```json
{
  "format": 1,
  "repo": "surface-pro-11",
  "name": "Surface Pro 11 extras",
  "summary": "What the repository is for, in a sentence or two.",
  "maintainer": { "name": "Brandon Williams", "github": "turbineBMW" },
  "homepage": "https://github.com/turbineBMW/surface-pro-11-omarchy",
  "status": "active",
  "packages": [
    {
      "name": "linux-omarchy",
      "replaces": true,
      "description": "Omarchy's 7.3 kernel with the Surface's suspend fix.",
      "upstream": "https://github.com/omacom/omarchy-pkgs/pull/1417"
    }
  ]
}
```

The installer shows `summary` and every package, with "replaces Omarchy's" on those marked `replaces` and the `upstream` link where the fix is headed. The maintainer keeps this file in their own repository (the Surface's is [`omarchy-recipe.json`](https://github.com/turbineBMW/surface-pro-11-omarchy/blob/main/omarchy-recipe.json)) and signs it as part of publishing.

## How the installer trusts it

The only thing trusted up front is the recipe's key fingerprint, reviewed here by PR. Everything else has to be signed by that key:

1. The Windows app takes the key from the recipe and refuses one whose fingerprint isn't the one named.
2. It downloads the database and the description and checks both signatures. It downloads every package, checks it against the database's SHA-256, and checks its signature. Anything that fails stops the extras, and the person can install Omarchy as it comes.
3. The stick carries the signatures. The Omarchy installer gives pacman a keyring that holds the recipe's key and nothing else, and installs with `SigLevel = Required DatabaseRequired`, so pacman checks it all again. A recipe whose key doesn't match its fingerprint is left out entirely.
4. After the install, the repository sits above `[omarchy]` in `/etc/pacman.conf` with its key trusted, so pacman checks every update the same way.

## Retiring and removing

Extras are meant to be temporary: once Omarchy has the same fixes, the repository should go.

**The maintainer retires it** by setting the description's `status` to `"retired"` with a `retiredMessage`, for example `"Omarchy 3.2 has everything this carried."`, and publishing it. Then:

- the installer stops offering the extras and shows the message;
- installed machines fetch the signed description daily (`omarchy-recipe-check.timer`), and every pacman transaction afterwards, `omarchy-update` included, ends with the notice and how to remove it.

**Anyone removes it from their machine** with:

```sh
omarchy-recipe                    # what it is and what came from it
sudo omarchy-recipe remove        # take it out of pacman, go back to Omarchy's packages
sudo omarchy-recipe remove --purge   # and remove the packages only it had
```

`remove` takes the repository out of `/etc/pacman.conf` and drops its key. It reinstalls Omarchy's own build of every package Omarchy also has (the kernel goes back to Omarchy's), and either keeps or (with `--purge`) removes the packages only the recipe had. It also turns off the daily check.

**The recipe leaves this repository** with a PR deleting its folder, once nobody needs the stick to offer it.

## Adding one

Open a pull request with the folder, after the repository publishes its signed description. `bin/check-recipes` checks the recipe (CI runs it), and `bin/fill-stick VOLUME --recipe ID` downloads and checks the whole repository the way the app does. A recipe hands its maintainer's key to pacman on every machine that accepts it, so reviews look at who maintains the repository and how it's built.
