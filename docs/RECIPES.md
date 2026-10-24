# Recipes

A recipe gives one laptop model extras that Omarchy doesn't carry yet: a kernel with fixes on their way upstream, firmware tuning, shell plugins. Its maintainer keeps a signed pacman repository; the recipe tells the installer when to offer it and what to install from it.

## A recipe

`recipes/<id>/recipe.json`, beside `recipes/<id>/key.asc` (the repository's public signing key):

```json
{
  "id": "microsoft-surface-pro-11",
  "name": "Surface Pro 11 extras",
  "device": "Microsoft Surface Pro, 11th Edition (Snapdragon X Elite, OLED)",
  "status": "tested",
  "maintainer": { "name": "Brandon Williams", "github": "turbineBMW" },
  "description": "This Surface's fixes and features while they make their way into Omarchy.",
  "features": ["Kernel with the Surface's suspend and Slim Pen fixes", "..."],
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
| `match.chids` | Which machines get the offer. Pick the narrowest CHID that covers the model and no other: type 4 (manufacturer, family, product, SKU) usually. `systemd-analyze chid` on Linux, or the hardware report, lists them. |
| `status` | `tested` once someone has installed with it on that model; `untested` says so in the installer. |
| `repo` | A pacman repository served over HTTPS, `$arch` expanded to `aarch64`, with a database named `<name>.db`. Its name must not be one of Omarchy's or Arch's. It goes above `[omarchy]`, so its packages replace Omarchy's of the same name. |
| `packages` | What to install. Every package in the repository goes on the stick, and installed packages the repository also has (a kernel) are replaced from it. |
| `firmware` | Driver-store files the model can't do without, beyond what the installer finds on its own: every non-driver file in the packages of the laptop's devices. The installer stops if one is missing. |
| `secureBoot`, `bootFromUsb` | The last screen's instructions for this model, instead of the general ones. |

## What happens with it

1. The Windows app matches the CHIDs, shows the recipe (its features, maintainer, repository and key) and lets the person leave it out.
2. It downloads the repository database and every package, checks each against the database's SHA-256, and copies them with `recipe.json` and `key.asc` to `OMARCHY_FW/recipe/`.
3. The Omarchy installer (with this repository's patches) installs the packages with the system, dependencies coming from its offline mirror. It then adds the repository above `[omarchy]` in `/etc/pacman.conf` and trusts the key. If a package needs something the image doesn't carry, a first-boot service installs it once the machine is online.

## Adding one

Open a pull request with the folder. `bin/check-recipes` checks it (CI runs it). Since a recipe hands its maintainer's key to pacman on everyone's machine who accepts it, reviews look at who maintains the repository and how it's built.
