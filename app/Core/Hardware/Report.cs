using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmarchyArmInstaller.Core.Hardware;

public sealed record FirmwareFile(string Name, long Size, string Sha256);

/// A driver package with firmware in it: what Linux would need from it, by
/// name and hash only, and where to look for the package again.
public sealed record FirmwarePackageInfo(
    string Folder, string? Inf, string? Provider, string? Version, string? Date,
    IReadOnlyList<string> Devices, string? CatalogSearch, IReadOnlyList<FirmwareFile> Files,
    IReadOnlyList<FirmwareFile> OtherFiles, IReadOnlyList<string> Infs);

/// A finished report: the zip, and what the person is shown and sends.
public sealed record HardwareReport(
    string ZipPath, string Id, SmbiosIdentity Identity, string? Processor, string? Panel,
    IReadOnlyList<ChidValue> Chids, IReadOnlyList<string> AcpiTables, int DeviceCount, int ProblemDevices,
    int FirmwareFiles, int FirmwarePackages, int InfFiles, int RegistryKeys, bool HasWindowsFacts,
    IReadOnlyList<string> Redactions, IReadOnlyList<string> Problems, string Summary)
{
    public string Model => string.Join(" ", new[] { Identity.Manufacturer, Identity.ProductName }.Where(s => !string.IsNullOrWhiteSpace(s)))
        is { Length: > 0 } m ? m : "Unknown computer";
}

/// Turns what a hardware source read into one zip a device-tree writer can
/// work from: ACPI tables, the SMBIOS table, CHIDs, EDIDs, the device list
/// with every property, each device's and driver's registry settings, the
/// resources drivers were given, the driver packages' INF files and the
/// firmware they carry, and what Windows' own tools say about storage, power
/// and displays, with everything that identifies this one machine (rather
/// than the model) taken out.
public static class ReportBuilder
{
    /// 2: every device property, registry keys, INF files and windows.json.
    public const int FormatVersion = 2;

    /// Tables that hold something personal, or that no device tree needs.
    static readonly Dictionary<string, string> LeftOutTables = new()
    {
        ["MSDM"] = "MSDM ACPI table (holds the Windows product key)",
        ["SLIC"] = "SLIC ACPI table (Windows activation)",
        ["WPBT"] = "WPBT ACPI table (a Windows program the firmware installs)",
    };

    /// Device enumerators for what was paired, plugged in or shared, rather
    /// than what the machine is: Bluetooth peers, printers, drives, volumes,
    /// network devices and the audio endpoints named after them.
    static readonly string[] PersonalEnumerators =
    [
        "BTH", "SWD\\PRINTENUM\\", "SWD\\WPDBUSENUM\\", "WPDBUSENUM\\", "STORAGE\\VOLUME\\", "USBSTOR\\",
        "SWD\\DAFUPNPPROVIDER\\", "SWD\\DAFWSDPROVIDER\\", "SWD\\DAFMCPPROVIDER\\", "UMB\\", "WSDPRINT\\",
        "MMDEVAPI\\", "SWD\\MMDEVAPI\\",
    ];

    /// Files in a driver package that are drivers or Windows plumbing rather
    /// than firmware the hardware loads.
    public static readonly HashSet<string> NotFirmware = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sys", ".dll", ".exe", ".cat", ".inf", ".pnf", ".mui", ".cpl", ".ocx", ".msi", ".ps1", ".bat", ".cmd",
        ".winmd", ".pdb", ".chm", ".ico", ".lnk",
    };

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// Registry binaries larger than this go in registry/blobs/ as files.
    const int InlineBlobBytes = 64 * 1024;

    public static HardwareReport Build(CollectedHardware hw, string folder, string toolVersion, IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var redactions = new List<string>();
        var problems = new List<string>(hw.Problems);
        var files = new Dictionary<string, byte[]>();

        // SMBIOS
        SmbiosIdentity identity = new(null, null, null, null, null, null, null, null, null, null, null, null, null);
        string? processor = null;
        var smbiosSecrets = new List<string>();
        if (hw.Smbios is { Length: > 0 })
        {
            var structures = Smbios.Parse(hw.Smbios);
            identity = Smbios.Identity(structures);
            processor = structures.FirstOrDefault(s => s.Type == 4)?.String(0x10)?.Trim();
            smbiosSecrets = Smbios.PersonalValues(structures);
            var (redacted, what) = Smbios.Redact(structures);
            redactions.AddRange(what);
            files["smbios/dmi.bin"] = DmidecodeDump(Smbios.Serialize(redacted), hw.SmbiosVersion);
        }
        else
        {
            problems.Add("The SMBIOS table could not be read.");
        }
        // Everything else gets the same secrets taken out, with matching placeholders.
        var scrub = new Scrubber(smbiosSecrets, hw.Secrets, redactions);

        // EDID: the panel's ID goes into the extra CHIDs.
        string? panel = null;
        var displays = new List<object>();
        var internalFirst = hw.Displays.OrderByDescending(d => d.Internal).ToList();
        for (var i = 0; i < internalFirst.Count; i++)
        {
            var d = internalFirst[i];
            var panelId = Edid.PanelId(d.Edid);
            if (d.Internal || internalFirst.Count == 1) panel ??= panelId;
            var name = $"edid/{i}-{panelId ?? "unknown"}{(d.Internal ? "-internal" : "")}.bin";
            files[name] = Edid.Redact(d.Edid);
            displays.Add(new { file = name, panel = panelId, @internal = d.Internal });
        }
        if (hw.Displays.Count > 0) redactions.Add("Display serial numbers in EDID");

        var chids = Chid.Calculate(identity, panel);

        // ACPI
        var acpi = new List<object>();
        var acpiNames = new List<string>();
        foreach (var group in hw.Acpi.GroupBy(t => t.Signature))
        {
            var tables = group.ToList();
            for (var i = 0; i < tables.Count; i++)
            {
                var t = tables[i];
                if (LeftOutTables.TryGetValue(t.Signature, out var why))
                {
                    redactions.Add(why);
                    continue;
                }
                var stem = Regex.Replace(t.Signature, "[^A-Za-z0-9]", "_").ToLowerInvariant() + (tables.Count > 1 ? (i + 1).ToString() : "");
                var file = $"acpi/{stem}.dat";
                files[file] = t.Data;
                acpiNames.Add(stem);
                acpi.Add(new
                {
                    signature = t.Signature,
                    file,
                    size = t.Data.Length,
                    revision = t.Data.Length > 8 ? t.Data[8] : 0,
                    oemId = t.Data.Length >= 16 ? Ascii(t.Data, 10, 6) : null,
                    oemTableId = t.Data.Length >= 24 ? Ascii(t.Data, 16, 8) : null,
                    oemRevision = t.Data.Length >= 28 ? BitConverter.ToUInt32(t.Data, 24) : 0,
                });
            }
        }
        if (hw.Acpi.Count == 0) problems.Add("No ACPI tables could be read.");

        // Devices: the list with every property; their registry keys go in registry/.
        var devices = RedactDevices(hw.Devices, scrub, redactions);
        files["devices.json"] = JsonSerializer.SerializeToUtf8Bytes(devices.Select(d => d with { EnumKey = null, DriverKey = null }), Json);

        // Firmware inventory: names and hashes, never the files; the INFs whole.
        status?.Report("Hashing the firmware in the drivers");
        var packages = FirmwareInventory(hw.Devices, files, ct);
        files["firmware.json"] = JsonSerializer.SerializeToUtf8Bytes(packages, Json);
        var infFiles = packages.Sum(p => p.Infs.Count);

        // Registry: each device's own key and its driver's, the drivers' services,
        // and the system-wide hardware description.
        status?.Report("Writing the registry keys");
        var registryKeys = 0;
        var deviceKeys = new SortedDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices.Where(d => d.EnumKey is not null || d.DriverKey is not null))
        {
            ct.ThrowIfCancellationRequested();
            var enumKey = d.EnumKey is null ? null : Externalize(d.EnumKey, files);
            var driverKey = d.DriverKey is null ? null : Externalize(d.DriverKey, files);
            registryKeys += (enumKey?.Count ?? 0) + (driverKey?.Count ?? 0);
            deviceKeys[d.InstanceId] = new { driver = d.Driver, enumKey, driverKey };
        }
        if (deviceKeys.Count > 0) files["registry/devices.json"] = JsonSerializer.SerializeToUtf8Bytes(deviceKeys, Json);
        var keptServices = new HashSet<string>(devices.Select(d => d.Service).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var services = new SortedDictionary<string, RegistryDump>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, dump) in hw.Services.Where(s => keptServices.Contains(s.Key)))
        {
            ct.ThrowIfCancellationRequested();
            services[name] = Externalize(scrub.Dump(dump), files);
            registryKeys += services[name].Count;
        }
        if (services.Count > 0) files["registry/services.json"] = JsonSerializer.SerializeToUtf8Bytes(services, Json);
        var system = new SortedDictionary<string, RegistryDump>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, dump) in hw.Registry)
        {
            ct.ThrowIfCancellationRequested();
            system[path] = Externalize(scrub.Dump(dump), files);
            registryKeys += system[path].Count;
        }
        if (system.Count > 0) files["registry/system.json"] = JsonSerializer.SerializeToUtf8Bytes(system, Json);
        if (scrub.DroppedValues > 0) redactions.Add($"{scrub.DroppedValues} registry values and properties named as serial numbers, addresses or identifiers");

        // What Windows computed as this machine's CHIDs, against this report's.
        List<string> windowsChids = [];
        if (hw.Registry.TryGetValue(@"SYSTEM\CurrentControlSet\Control\SystemInformation", out var info)
            && info.Values.TryGetValue("ComputerHardwareIds", out var ids) && ids.Value is string[] list)
        {
            windowsChids = list.Select(x => x.Trim('{', '}').ToLowerInvariant()).ToList();
            var ours = chids.Select(c => c.Value.ToString().ToLowerInvariant()).ToHashSet();
            var missing = windowsChids.Where(x => !ours.Contains(x)).ToList();
            if (missing.Count > 0) problems.Add($"Windows lists {missing.Count} hardware IDs this report did not compute: {string.Join(", ", missing)}");
        }

        // Windows' own view: storage, battery, power states, GPU, displays, the driver store.
        JsonNode? windows = null;
        if (hw.WindowsJson is { Length: > 0 } json)
        {
            try
            {
                windows = JsonNode.Parse(json);
                scrub.Json(windows);
                files["windows.json"] = Encoding.UTF8.GetBytes(windows!.ToJsonString(Json));
            }
            catch (JsonException e)
            {
                problems.Add($"Windows' view of the hardware could not be read: {e.Message}");
                windows = null;
            }
        }

        var model = string.Join(" ", new[] { identity.Manufacturer, identity.ProductName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        files["hwids.json"] = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "devicetree",
            name = model,
            compatible = "",
            // systemd's hwids files leave out the manufacturer-wide CHIDs (11-14).
            hwids = chids.Where(c => c.Type is not ("11" or "12" or "13" or "14")).Select(c => c.Value.ToString()).ToList(),
        }, Json);

        var created = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var report = new
        {
            format = FormatVersion,
            tool = toolVersion,
            id,
            created,
            os = hw.Os,
            identity,
            processor,
            panel,
            chids = chids.Select(c => new { type = c.Type, fields = c.Fields, value = c.Value.ToString() }),
            windowsChids,
            smbiosVersion = $"{hw.SmbiosVersion.Major}.{hw.SmbiosVersion.Minor}",
            acpi,
            displays,
            redactions = redactions.Distinct().ToList(),
            problems,
        };
        files["report.json"] = JsonSerializer.SerializeToUtf8Bytes(report, Json);

        var problemDevices = devices.Count(d => d.Problem is > 0);
        var firmwareFiles = packages.Sum(p => p.Files.Count);
        var summary = Summary(id, created, toolVersion, hw.Os, identity, processor, panel, chids, acpiNames,
            devices.Count, problemDevices, firmwareFiles, packages.Count, infFiles, registryKeys, windows is not null,
            redactions.Distinct().ToList(), problems);
        files["summary.md"] = Encoding.UTF8.GetBytes(summary);

        Directory.CreateDirectory(folder);
        var slug = Slug(model.Length > 0 ? model : "unknown");
        var zip = Path.Combine(folder, $"hardware-report-{slug}-{id}.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var (name, data) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(data);
            }
        }

        return new HardwareReport(zip, id, identity, processor, panel, chids, acpiNames, devices.Count, problemDevices,
            firmwareFiles, packages.Count, infFiles, registryKeys, windows is not null, redactions.Distinct().ToList(), problems, summary);
    }

    /// A dump with its large binaries written as files under registry/blobs/.
    static RegistryDump Externalize(RegistryDump dump, Dictionary<string, byte[]> files)
    {
        var result = new RegistryDump();
        foreach (var (name, v) in dump.Values)
        {
            if (v.Value is byte[] { Length: > InlineBlobBytes } data)
            {
                var sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
                var file = $"registry/blobs/{sha}.bin";
                files[file] = data;
                result.Values[name] = v with { Value = new { file, size = data.Length, sha256 = sha } };
            }
            else result.Values[name] = v;
        }
        foreach (var (name, key) in dump.Keys) result.Keys[name] = Externalize(key, files);
        return result;
    }

    public static string Slug(string text) =>
        Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') is { Length: > 0 } s ? s : "unknown";

    static string Ascii(byte[] data, int offset, int length) => Encoding.ASCII.GetString(data, offset, length).TrimEnd('\0', ' ');

    /// The table behind a synthesised SMBIOS 3 entry point, laid out the way
    /// dmidecode --dump-bin writes it, so dmidecode --from-dump reads it.
    static byte[] DmidecodeDump(byte[] table, (byte Major, byte Minor, byte Revision) version)
    {
        var dump = new byte[32 + table.Length];
        "_SM3_"u8.CopyTo(dump);
        dump[6] = 0x18;
        dump[7] = version.Major == 0 ? (byte)3 : version.Major;
        dump[8] = version.Minor;
        dump[9] = version.Revision;
        dump[10] = 1; // entry point revision
        BitConverter.GetBytes((uint)table.Length).CopyTo(dump, 12);
        BitConverter.GetBytes(32UL).CopyTo(dump, 16);
        byte sum = 0;
        for (var i = 0; i < 0x18; i++) sum += dump[i];
        dump[5] = (byte)(0x100 - sum);
        table.CopyTo(dump, 32);
        return dump;
    }

    // ---------- Devices ----------

    public static List<DeviceInfo> RedactDevices(IReadOnlyList<DeviceInfo> all, Scrubber scrub, List<string> redactions)
    {
        var dropped = new HashSet<string>(all.Where(d => PersonalEnumerators.Any(p => d.InstanceId.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Select(d => d.InstanceId), StringComparer.OrdinalIgnoreCase);
        // Children of a left-out device go too (a paired headset's endpoints).
        var byId = all.GroupBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        bool Dropped(DeviceInfo d)
        {
            for (var (cur, depth) = (d, 0); cur is not null && depth < 64; depth++)
            {
                if (dropped.Contains(cur.InstanceId)) return true;
                cur = cur.Parent is { } p && byId.TryGetValue(p, out var parent) ? parent : null;
            }
            return false;
        }
        var kept = all.Where(d => !Dropped(d)).ToList();
        var left = all.Count - kept.Count;
        if (left > 0) redactions.Add($"{left} Bluetooth, printer, drive, network and audio-endpoint devices");

        // The same secret gets the same placeholder, so parent links still match.
        string Redact(string value) => scrub.Text(value);
        string? R(string? v) => v is null ? null : Redact(v);

        return kept.Select(d => d with
        {
            InstanceId = Redact(d.InstanceId),
            Parent = R(d.Parent),
            Name = R(d.Name),
            Manufacturer = R(d.Manufacturer),
            HardwareIds = d.HardwareIds.Select(Redact).ToArray(),
            CompatibleIds = d.CompatibleIds.Select(Redact).ToArray(),
            LocationPaths = d.LocationPaths.Select(Redact).ToArray(),
            DriverFolder = d.DriverFolder is null ? null : Path.GetFileName(d.DriverFolder.TrimEnd('\\', '/')),
            Properties = d.Properties.Select(scrub.Property).ToList(),
            EnumKey = d.EnumKey is null ? null : scrub.Dump(d.EnumKey),
            DriverKey = d.DriverKey is null ? null : scrub.Dump(d.DriverKey),
        }).OrderBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------- Firmware ----------

    /// Every file in the driver packages of the present devices: the firmware
    /// by name, size and hash, the drivers and plumbing the same way, and the
    /// INF files themselves, which say which device loads which firmware and
    /// what settings its driver gets, added to files under drivers/.
    public static List<FirmwarePackageInfo> FirmwareInventory(IReadOnlyList<DeviceInfo> devices, Dictionary<string, byte[]> files,
        CancellationToken ct)
    {
        var result = new List<FirmwarePackageInfo>();
        foreach (var group in devices.Where(d => d.DriverFolder is not null)
                     .GroupBy(d => d.DriverFolder!, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(group.Key)) continue;
            var folder = Path.GetFileName(group.Key.TrimEnd('\\', '/'));
            var found = new List<FirmwareFile>();
            var others = new List<FirmwareFile>();
            var infs = new List<string>();
            foreach (var path in Directory.EnumerateFiles(group.Key, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(group.Key, path).Replace('\\', '/');
                try
                {
                    using var stream = File.OpenRead(path);
                    var file = new FirmwareFile(relative, stream.Length, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
                    (NotFirmware.Contains(Path.GetExtension(path)) ? others : found).Add(file);
                    if (Path.GetExtension(path).Equals(".inf", StringComparison.OrdinalIgnoreCase) && stream.Length <= 4 * 1024 * 1024)
                    {
                        stream.Position = 0;
                        var text = new byte[stream.Length];
                        stream.ReadExactly(text);
                        var name = $"drivers/{folder}/{relative}";
                        files[name] = text;
                        infs.Add(name);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.Info($"Firmware: could not hash {path}: {e.Message}");
                }
            }
            if (found.Count == 0 && others.Count == 0) continue;
            var first = group.First();
            var hwid = group.SelectMany(d => d.HardwareIds).FirstOrDefault();
            result.Add(new FirmwarePackageInfo(
                folder, first.Inf, first.DriverProvider, first.DriverVersion, first.DriverDate,
                group.Select(d => d.Name ?? d.InstanceId).Distinct().Order().ToList(),
                hwid is null ? null : $"https://www.catalog.update.microsoft.com/Search.aspx?q={Uri.EscapeDataString(hwid)}",
                found, others, infs));
        }
        return result.OrderBy(p => p.Folder, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------- Summary ----------

    static string Summary(string id, string created, string tool, string os, SmbiosIdentity idn, string? processor, string? panel,
        IReadOnlyList<ChidValue> chids, IReadOnlyList<string> acpi, int devices, int problemDevices, int firmwareFiles,
        int packages, int infFiles, int registryKeys, bool windows, IReadOnlyList<string> redactions, IReadOnlyList<string> problems)
    {
        var s = new StringBuilder();
        var model = string.Join(" ", new[] { idn.Manufacturer, idn.ProductName }.Where(x => !string.IsNullOrWhiteSpace(x)));
        s.AppendLine($"# Hardware report: {(model.Length > 0 ? model : "unknown computer")}");
        s.AppendLine();
        s.AppendLine("| | |");
        s.AppendLine("|---|---|");
        void Row(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) s.AppendLine($"| {k} | {v.Replace("|", "\\|")} |"); }
        Row("Manufacturer", idn.Manufacturer);
        Row("Product", idn.ProductName);
        Row("SKU", idn.Sku);
        Row("Family", idn.Family);
        Row("Version", idn.Version);
        Row("Baseboard", string.Join(" ", new[] { idn.BaseboardManufacturer, idn.BaseboardProduct }.Where(x => x is not null)));
        Row("Firmware", $"{idn.BiosVendor} {idn.BiosVersion}{(idn.BiosDate is null ? "" : $" ({idn.BiosDate})")}".Trim());
        Row("Processor", processor);
        Row("Panel", panel);
        Row("Windows", os);
        Row("Report", $"{id}, {created}, tool {tool}");
        s.AppendLine();
        s.AppendLine("## Hardware IDs (CHIDs)");
        s.AppendLine();
        s.AppendLine("```");
        foreach (var c in chids) s.AppendLine($"{c.Type,4} {c.Fields} {c.Value}");
        s.AppendLine("```");
        s.AppendLine();
        s.AppendLine("Fields: M manufacturer, F family, P product, S SKU, m/p baseboard manufacturer/product, B/v/R/r BIOS vendor/version/major/minor, e enclosure, E panel EDID. The same values `systemd-analyze chid` prints; `hwids.json` is ready for systemd's `src/boot/hwids` once the device tree's compatible is filled in.");
        s.AppendLine();
        s.AppendLine("## Contents");
        s.AppendLine();
        s.AppendLine($"- `acpi/`: {acpi.Count} ACPI tables ({string.Join(", ", acpi)}). Disassemble with `iasl -d acpi/dsdt.dat`.");
        s.AppendLine("- `smbios/dmi.bin`: the SMBIOS table; read it with `dmidecode --from-dump smbios/dmi.bin`.");
        s.AppendLine("- `edid/`: display EDIDs.");
        s.AppendLine($"- `devices.json`: {devices} devices with hardware IDs, ACPI paths, drivers and every property the configuration manager has for them{(problemDevices > 0 ? $"; {problemDevices} without a working driver" : "")}. Binary values are base64.");
        s.AppendLine($"- `firmware.json`: {firmwareFiles} firmware files in {packages} driver packages, by name, size and SHA-256, with a Microsoft Update Catalog search for each package, and the packages' other files the same way. The firmware itself is not included.");
        s.AppendLine($"- `drivers/`: the {infFiles} INF files of those packages, which say which device loads which firmware and what settings its driver gets.");
        s.AppendLine($"- `registry/`: {registryKeys} registry keys. `devices.json` holds each device's own key (Device Parameters, LogConf) and its driver's settings key under Control\\Class; `services.json` the drivers' service keys; `system.json` HARDWARE\\DESCRIPTION\\System (processors, firmware, configuration data), DEVICEMAP, RESOURCEMAP (the memory, interrupts and bus connections each driver was given, decoded beside the raw value), SystemInformation (Windows' own CHIDs) and Secure Boot's state. Binaries over 64 KiB are in `blobs/`.");
        if (windows) s.AppendLine("- `windows.json`: what Windows' tools report: the storage bus and disks, the battery, the power states the firmware offers (`powercfg /a`), the GPU and display modes, network adapters, thermal zones, the TPM, and every third-party package in the driver store.");
        s.AppendLine();
        s.AppendLine("## Left out for privacy");
        s.AppendLine();
        foreach (var r in redactions) s.AppendLine($"- {r}");
        if (problems.Count > 0)
        {
            s.AppendLine();
            s.AppendLine("## Could not be read");
            s.AppendLine();
            foreach (var p in problems) s.AppendLine($"- {p}");
        }
        return s.ToString();
    }
}
