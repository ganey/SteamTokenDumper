using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using Spectre.Console;
using ValveKeyValue;

namespace SteamTokenDumper;

internal static class SteamClientData
{
    public static void ReadFromSteamClient(Payload payload, KnownDepotIds knownDepotIds)
    {
        var table = new Table
        {
            Title = new("Steam client"),
            Border = TableBorder.Rounded
        };

        AnsiConsole.Live(table)
            .Start(ctx =>
            {
                table.AddColumn("Reading tokens from Steam client files");

                var steamLocation = GetSteamPath();

                if (steamLocation == default)
                {
                    table.AddRow("Did not find Steam client.");
                    return;
                }

                table.AddRow(new Text($"Found Steam at {steamLocation}"));
                ctx.Refresh();

                try
                {
                    var shardDirectory = Path.Join(steamLocation, "appcache", "appinfo");

                    if (Directory.Exists(shardDirectory))
                    {
                        ReadAppInfoShards(table, payload, shardDirectory);
                    }
                    else
                    {
                        ReadAppInfo(table, payload, Path.Join(steamLocation, "appcache", "appinfo.vdf"));
                    }
                }
                catch (Exception e)
                {
                    table.AddRow(new Text($"Failed to parse appinfo: {e}", new Style(Color.Red)));
                }

                ctx.Refresh();

                try
                {
                    ReadPackageInfo(table, payload, Path.Join(steamLocation, "appcache", "packageinfo.vdf"));
                }
                catch (Exception e)
                {
                    table.AddRow(new Text($"Failed to parse packageinfo: {e}", new Style(Color.Red)));
                }

                ctx.Refresh();

                try
                {
                    ReadDepotKeys(table, payload, knownDepotIds, Path.Join(steamLocation, "config", "config.vdf"));
                }
                catch (Exception e)
                {
                    table.AddRow(new Text($"Failed to parse config: {e}", new Style(Color.Red)));
                }
            });
    }

    private static void ReadAppInfo(Table table, Payload payload, string filename)
    {
        using var fs = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(fs);
        var magic = reader.ReadUInt32();

        if (magic is not 0x07_56_44_27 and not 0x07_56_44_28 and not 0x07_56_44_29)
        {
            throw new InvalidDataException($"Unknown appinfo.vdf magic: {magic:X}");
        }

        fs.Position += 4; // universe

        if (magic == 0x07_56_44_29)
        {
            fs.Position += 8; // offset to string pool
        }

        do
        {
            var appid = reader.ReadUInt32();

            if (appid == 0)
            {
                break;
            }

            var nextOffset = reader.ReadUInt32() + fs.Position; // size

            fs.Position += 4 + 4; // infoState + lastUpdated

            var token = reader.ReadUInt64();

            if (token > 0)
            {
                payload.Apps[appid.ToString(CultureInfo.InvariantCulture)] = token.ToString(CultureInfo.InvariantCulture);
            }

            fs.Position = nextOffset;
        } while (true);

        table.AddRow($"Got {payload.Apps.Count} app tokens from appinfo.vdf");
    }

    // Newer Steam clients split the app info cache into appcache/appinfo/appinfo_XX.vdf shards.
    // Each shard has a 44 byte header followed by a fixed size (80 byte) index with the app token.
    private static void ReadAppInfoShards(Table table, Payload payload, string directory)
    {
        const uint ShardMagic = 0x07_56_44_2A;
        const int HeaderSize = 44;
        const int EntrySize = 80;
        const int TokenOffsetInEntry = 24;

        var files = Directory.GetFiles(directory, "appinfo_*.vdf");

        if (files.Length == 0)
        {
            throw new InvalidDataException("No appinfo shard files found");
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);

            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(fs);

            if (fs.Length < HeaderSize)
            {
                throw new InvalidDataException($"Truncated appinfo shard {name}");
            }

            var magic = reader.ReadUInt32();

            if (magic != ShardMagic)
            {
                throw new InvalidDataException($"Unknown appinfo shard magic in {name}: {magic:X}");
            }

            fs.Position = 16;
            var entryCount = reader.ReadUInt32();
            var indexSize = reader.ReadUInt32();

            if (indexSize != (long)entryCount * EntrySize || HeaderSize + (long)indexSize > fs.Length)
            {
                throw new InvalidDataException($"Corrupt appinfo shard index in {name}");
            }

            for (var i = 0; i < entryCount; i++)
            {
                var entryStart = HeaderSize + ((long)i * EntrySize);

                fs.Position = entryStart;
                var appid = reader.ReadUInt32();

                fs.Position = entryStart + TokenOffsetInEntry;
                var token = reader.ReadUInt64();

                if (token > 0)
                {
                    payload.Apps[appid.ToString(CultureInfo.InvariantCulture)] = token.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        if (files.Length != 256)
        {
            table.AddRow(new Text($"Expected 256 appinfo shards but found {files.Length}", new Style(Color.Yellow)));
        }

        table.AddRow($"Got {payload.Apps.Count} app tokens from {files.Length} appinfo shards");
    }

    private static void ReadPackageInfo(Table table, Payload payload, string filename)
    {
        using var fs = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(fs);
        var magic = reader.ReadUInt32();

        if (magic == 0x06_56_55_27)
        {
            table.AddRow("Old Steam client has no package tokens in packageinfo.vdf, skipping");
            return;
        }

        if (magic != 0x06_56_55_28)
        {
            throw new InvalidDataException($"Unknown packageinfo.vdf magic: {magic:X}");
        }

        reader.ReadUInt32(); // universe

        var deserializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Binary);

        do
        {
            var subid = reader.ReadUInt32();

            if (subid == 0xFFFFFFFF)
            {
                break;
            }

            fs.Position += 20 + 4;

            var token = reader.ReadUInt64();

            if (token > 0)
            {
                payload.Subs[subid.ToString(CultureInfo.InvariantCulture)] = token.ToString(CultureInfo.InvariantCulture);
            }

            deserializer.Deserialize(fs);
        } while (true);

        table.AddRow($"Got {payload.Subs.Count} package tokens from packageinfo.vdf");
    }

    private static void ReadDepotKeys(Table table, Payload payload, KnownDepotIds knownDepotIds, string filename)
    {
        KVObject data;

        using (var fs = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            data = KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Deserialize(fs, new KVSerializerOptions
            {
                HasEscapeSequences = true,
            });
        }

        // For some inexplicable reason these keys can have different capitalizations
        var depots = (data.Children
            ?.FirstOrDefault(k => k.Name.Equals("software", StringComparison.OrdinalIgnoreCase))
            ?.FirstOrDefault(k => k.Name.Equals("valve", StringComparison.OrdinalIgnoreCase))
            ?.FirstOrDefault(k => k.Name.Equals("steam", StringComparison.OrdinalIgnoreCase))
            ?.FirstOrDefault(k => k.Name.Equals("depots", StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidDataException("Failed to find depots section in config.vdf");

        foreach (var depot in depots)
        {
            var depotKey = depot["DecryptionKey"];

            if (depotKey != null)
            {
                var depotId = uint.Parse(depot.Name, CultureInfo.InvariantCulture);

                if (knownDepotIds.PreviouslySent.Contains(depotId) || knownDepotIds.Server.Contains(depotId))
                {
                    continue;
                }

                var depotKeyString = depotKey.ToString(CultureInfo.InvariantCulture).ToUpperInvariant();

                if (depotKeyString.Length != 64 || depotKeyString.Any(static x => !char.IsAsciiHexDigitUpper(x)))
                {
                    throw new InvalidDataException($"Corrupted depot key");
                }

                payload.Depots[depot.Name] = depotKeyString;
            }
        }

        table.AddRow($"Got {depots.Count()} depot keys from config.vdf");
    }

    private static string? GetSteamPath()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Valve\\Steam") ??
                            Registry.LocalMachine.OpenSubKey("SOFTWARE\\Valve\\Steam");

            if (key?.GetValue("SteamPath") is string steamPath)
            {
                return steamPath;
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var paths = new[] { ".steam", ".steam/steam", ".steam/root", ".local/share/Steam" };

            return paths
                .Select(path => Path.Join(home, path))
                .FirstOrDefault(steamPath => Directory.Exists(Path.Join(steamPath, "appcache")));
        }
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Join(home, "Steam");
        }

        return default;
    }
}
