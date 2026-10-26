# Omarchy Arm Installer

Installs [Omarchy](https://omarchy.org) on Snapdragon laptops from their own Windows, and collects what Linux developers need to support the ones it can't yet.

Run `OmarchyArmInstaller.exe` in the laptop's Windows (as administrator). It reads the laptop first, and does one of two things:

- **Make an Omarchy USB stick**, when Linux has a device tree for the laptop. It recognises the laptop by its hardware IDs, copies the firmware Linux needs out of Windows' own drivers onto the stick, and writes the installer. BitLocker stays on: the firmware comes from the running Windows, not from its encrypted disk. Windows can stay next to Omarchy (you shrink C: in Disk Management, and the installer checks the room) or make way for it. If the laptop has a [recipe](#recipes), its extras can come along too.
- **Share a hardware report**, when it doesn't: the installer says so, and what a report does and doesn't buy. The report holds everything Windows and the firmware expose about the hardware: the ACPI and SMBIOS tables, hardware IDs, every device with all its properties, each device's and driver's registry settings and the resources drivers were given, the driver packages' INF files and a list of the firmware in them, and what Windows' tools say about storage, battery, power states, displays and the driver store, saved as a zip with serial numbers and other personal details taken out, and sent to [omarchy-hardware-reports](https://github.com/turbineBMW/omarchy-hardware-reports) through an issue form.

## How a laptop is recognised

The installer computes the laptop's CHIDs, the hardware IDs systemd-stub picks a device tree by, from its SMBIOS table and panel EDID. Then:

| The laptop | What the installer does |
|---|---|
| Has a recipe | Offers the recipe's extras, then makes the stick |
| Is in systemd's [hwids](https://github.com/systemd/systemd/tree/main/src/boot/hwids/aa64) (a device tree exists) | Makes the stick with Omarchy as it comes |
| Neither | Says it can't start Omarchy yet, explains the device tree, and offers the hardware report |
| Can't be read | Lets the user choose either |

`app/Assets/models.json` is systemd's list; `bin/update-models` refreshes it.

## Recipes

A recipe adds a model's own packages while they make their way into Omarchy: a patched kernel, plugins, tuning. **These extras are not part of Omarchy**; they come from the recipe's maintainer, and the installer says so before anyone opts in. A recipe is a folder in [`recipes/`](recipes) with a `recipe.json` and its repository's signing key. The repository describes itself in a signed `<repo>.recipe.json`, and the installer shows what each package does from that file.

Everything the installer takes from a recipe's repository must be signed by the key the recipe pins: the database, the description and every package. pacman checks them again when Omarchy installs them. Installed machines get `omarchy-recipe`, which shows what came from the repository and removes it again (`sudo omarchy-recipe remove`). When the maintainer retires the repository because Omarchy has caught up, Omarchy's updates say so. See [docs/RECIPES.md](docs/RECIPES.md).

## The stick

The installer image is Omarchy's aarch64 ISO with four patches ([`iso/patches`](iso/patches)) that read a 1 GiB `OMARCHY_FW` partition on the stick:

```
OMARCHY_FW/
  DriverStore/FileRepository/<package>/...   firmware, staged with qcom-firmware-extract
  recipe/recipe.json                         the chosen recipe, if any
  recipe/key.asc                             its key
  recipe/info.json, info.json.sig            the repository's signed description
  recipe/repo/<name>.db(.sig), *.pkg.tar.*(.sig)
  recipe/official/*.pkg.tar.*(.sig)          Omarchy's and Arch's packages the recipe needs and the image lacks
```

The patches are meant for omarchy-iso itself; once they are there, the stick is the stock Omarchy ISO.

## Building

```sh
dotnet publish app -c Release -r win-arm64 --self-contained true -o out   # the Windows app
bin/build-iso                                                             # the ISO
bin/publish-iso ISO [out/OmarchyArmInstaller.exe]                         # a release the app finds (needs unsquashfs)
sudo bin/make-usb ISO /dev/sdX --firmware .../FileRepository --recipe ID   # a stick from Linux
bin/check-recipes
```

Off Windows the app runs as a demo that writes nothing; `OMARCHY_DEMO_DMI`, `OMARCHY_DEMO_DRIVER_STORE`, `OMARCHY_DEMO_RECIPE_SERVER`, `OMARCHY_DEMO_MODE` (install or report), `OMARCHY_DEMO_STEP` (0 in the report collects it and stays) and `OMARCHY_DEMO_SCROLL=end` feed it. Logs go to `%LOCALAPPDATA%\OmarchyArmInstaller\Logs`.
