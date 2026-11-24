using PKHeX.Core;
using PermuteMMO.Lib;
using System.Globalization;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System;
using System.Linq;
using static SysBot.Base.SwitchButton;
using static SysBot.Base.SwitchStick;
using static SysBot.Pokemon.PokeDataOffsetsLA;
using SysBot.Base;

namespace SysBot.Pokemon;

public sealed class ArceusBot : PokeRoutineExecutor8LA, IEncounterBot
{
    private readonly PokeTradeHub<PA8> Hub;
    private readonly IDumper DumpSetting;
    private readonly int[] DesiredMinIVs;
    private readonly int[] DesiredMaxIVs;
    private readonly ArceusBotSettings Settings;
    public ICountSettings Counts => Settings;

    public ArceusBot(PokeBotState cfg, PokeTradeHub<PA8> hub) : base(cfg)
    {
        Hub = hub;
        Settings = Hub.Config.ArceusLA;
        DumpSetting = Hub.Config.Folder;
        StopConditionSettings.InitializeTargetIVs(Hub.Config, out DesiredMinIVs, out DesiredMaxIVs);
    }

    private ulong MainNsoBase;
    private ulong OverworldOffset;
    private (string, string, string) coordinates;
    private List<PA8> boxlist = [];
    private bool HasCharm = false;

    private static readonly string[] ObsidianTitle =
    {
        "Rapidash","Snorlax","Luxio","Floatzel","Staravia","Parasect","Kricketune","Stantler","Bibarel","Scyther","Lopunny","Graveler","Blissey","Heracross","Magikarp","Infernape","Alakazam","Gyarados",
    };

    private static readonly string[] CrimsonTitle =
    {
        "Tangrowth","Hippowdon","Skuntank","Onix","Rhyhorn","Honchkrow","Roserade","Lickilicky","Pachirisu","Carnivine","Vespiquen","Yanmega","Ursaring","Toxicroak","Torterra","Sliggoo","Raichu","Ursaring","Whiscash",
    };

    private static readonly string[] CoronetTitle =
    {
        "Mothim","Bronzong","Carnivine","Gligar","Gabite","Luxray","Electivire","Goodra","Steelix","Clefable","Golem","Mismagius","Rhyperior","Probopass","Gliscor",
    };

    private static readonly string[] CobaltTitle =
    {
        "Walrein","Drapion","Purugly","Ambipom","Golduck","Dusknoir","Machoke","Octillery","Mantine","Tentacruel","Ninetales","Chansey","Lumineon","Gyarados","Gastrodon","Qwilfish","Empoleon","Mothim",
    };

    private static readonly string[] AlabasterTitle =
    {
        "Glalie","Abomasnow","Mamoswine","Gardevoir","Sneasel","Chimecho","Machamp","Swinub","Piloswine","Lucario","Electabuzz","Froslass","Garchomp",
    };

    public override async Task MainLoop(CancellationToken token)
    {
        Log("Identifying trainer data of the host console.");
        await IdentifyTrainer(token).ConfigureAwait(false);
        PokedexSaveData? dex = null;
        await InitializeHardware(Settings, token).ConfigureAwait(false);
        try
        {
            Log($"Starting main {GetType().Name} loop.");
            Config.IterateNextRoutine();
            if (Settings.BotType != ArceusMode.GenieScanner && Settings.BotType != ArceusMode.OtherLegendReset)
                HasCharm = await CheckForCharm(token).ConfigureAwait(false);

            if (Settings.BotType == ArceusMode.DistortionReader || Settings.BotType == ArceusMode.MassiveOutbreakHunter || Settings.BotType == ArceusMode.MultiSpawnPathSearch)
                dex = await ReadPokedex(token).ConfigureAwait(false);

            await ResetStick(token).ConfigureAwait(false);

            if (Settings.SpecialConditions.UsePlayerInvincibleCheat)
            {
                Log("Checking MainNSOBase...");
                MainNsoBase = await SwitchConnection.GetMainNsoBaseAsync(token).ConfigureAwait(false);
            }

            if (!Settings.SpecialConditions.UsePlayerInvincibleCheat && Settings.BotType != ArceusMode.OtherLegendReset || !Settings.SpecialConditions.UsePlayerInvincibleCheat && Settings.BotType != ArceusMode.GenieScanner)
            {
                Log("Checking MainNSOBase...");
                MainNsoBase = await SwitchConnection.GetMainNsoBaseAsync(token).ConfigureAwait(false);
            }

            var task = Hub.Config.ArceusLA.BotType switch
            {
                ArceusMode.PlayerCoordScan => PlayerCoordScan(token),
                ArceusMode.SeedAdvancer => SeedAdvancer(token),
                ArceusMode.TimeSeedAdvancer => TimeSeedAdvancer(token),
                ArceusMode.StaticAlphaScan => ScanForSpawners(token),
                ArceusMode.DistortionSpammer => DistortionSpammer(token),
                ArceusMode.DistortionReader => DistortionReader(dex!, token),
                ArceusMode.MassiveOutbreakHunter => MassiveOutbreakHunter(dex!, token),
                ArceusMode.MultiSpawnPathSearch => PerformMultiSpawnerScan(dex!, token),
                ArceusMode.GenieScanner => ScanForSpawners(token),
                //ArceusMode.ManaphyReset => ManaphyReset(token),
                ArceusMode.OtherLegendReset => ScanForSpawners(token),
                ArceusMode.AdvanceTimeDelay => AdvanceTimeDelay(token),
                _ => PlayerCoordScan(token),
            };
            await task.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log(e.Message);
        }

        Log($"Ending {GetType().Name} loop.");
        await HardStop().ConfigureAwait(false);
    }

    public override async Task HardStop()
    {
        await ResetStick(CancellationToken.None).ConfigureAwait(false);
        await CleanExit(CancellationToken.None).ConfigureAwait(false);
    }

    private bool IsWaiting;
    private bool IsWaitingConfirmation;

    private static readonly object FileLock = new();

    private string ConsoleReportFolder(string root)
    {
        var label = string.Concat((Connection.Label ?? string.Empty).Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
        if (string.IsNullOrWhiteSpace(label))
            label = "console";
        var cname = string.Concat((Connection.Name ?? string.Empty).Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
        if (!string.IsNullOrWhiteSpace(cname) && !label.Contains(cname))
            label += "_" + cname;
        var dir = System.IO.Path.Combine(AppContext.BaseDirectory, root, label);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    private void SafeAppendLine(string file, string line)
    {
        for (int attempt = 1; attempt <= 60; attempt++)
        {
            try
            {
                lock (FileLock)
                {
                    using var fs = new System.IO.FileStream(file, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
                    using var sw = new System.IO.StreamWriter(fs);
                    sw.WriteLine(line);
                }
                return;
            }
            catch (System.IO.IOException) when (attempt < 60) { Thread.Sleep(500); }
            catch (Exception ex) { Log($"[Files] Could not write to {file} ({ex.Message}). The hunt carries on."); return; }
        }
    }

    private void SafeWriteAllText(string file, string text)
    {
        for (int attempt = 1; attempt <= 60; attempt++)
        {
            try
            {
                lock (FileLock)
                {
                    using var fs = new System.IO.FileStream(file, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
                    using var sw = new System.IO.StreamWriter(fs);
                    sw.Write(text);
                }
                return;
            }
            catch (System.IO.IOException) when (attempt < 60) { Thread.Sleep(500); }
            catch (Exception ex) { Log($"[Files] Could not write {file} ({ex.Message}). The hunt carries on."); return; }
        }
    }

    public void Acknowledge() => IsWaiting = false;
    public void AcknowledgeConfirmation() => IsWaitingConfirmation = false;

    private async Task TimeTest(CancellationToken token)
    {
        var timeofs = await NewParsePointer(TimePtrLA, token).ConfigureAwait(false);
        var timeVal = BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(timeofs, 2, token).ConfigureAwait(false), 0);
        if (timeVal <= 7)
            timeVal = 15;
        else timeVal = 00;

        byte[] timeByte = BitConverter.GetBytes(timeVal);
        await SwitchConnection.WriteBytesAbsoluteAsync(timeByte, timeofs, token).ConfigureAwait(false);
    }

    private async Task AdvanceTimeDelay(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Log("Changing time to advance frames...");
            await TimeTest(token).ConfigureAwait(false);
            Log("Waiting for confirmation before advancing again...");
            IsWaitingConfirmation = true;
            while (IsWaitingConfirmation)
                await Task.Delay(0_500, token).ConfigureAwait(false);
        }
    }

    private async Task PlayerCoordScan(CancellationToken token)
    {
        var ofs = await NewParsePointer(PlayerCoordPtrLA, token).ConfigureAwait(false);
        var coord = BitConverter.ToUInt32(await SwitchConnection.ReadBytesAbsoluteAsync(ofs, 4, token).ConfigureAwait(false), 0);
        var coord2 = BitConverter.ToUInt32(await SwitchConnection.ReadBytesAbsoluteAsync(ofs + 0x4, 4, token).ConfigureAwait(false), 0);
        var coord3 = BitConverter.ToUInt32(await SwitchConnection.ReadBytesAbsoluteAsync(ofs + 0x8, 4, token).ConfigureAwait(false), 0);

        Log($"Current Player Coords - X: {coord:X8} Y: {coord2:X8} Z: {coord3:X8}");

        if (Settings.AlphaScanConditions.AutoFillCoords == ArceusAutoFill.CampZone)
        {
            Log($"Autofilling CampZone XYZ");
            Settings.SpecialConditions.CampZoneX = $"{coord:X8}";
            Settings.SpecialConditions.CampZoneY = $"{coord2:X8}";
            Settings.SpecialConditions.CampZoneZ = $"{coord3:X8}";
        }

        if (Settings.AlphaScanConditions.AutoFillCoords == ArceusAutoFill.SpawnZone)
        {
            Log($"Autofilling SpawnZone XYZ");
            Settings.SpecialConditions.SpawnZoneX = $"{coord:X8}";
            Settings.SpecialConditions.SpawnZoneY = $"{coord2:X8}";
            Settings.SpecialConditions.SpawnZoneZ = $"{coord3:X8}";
        }
    }

    private async Task Reposition(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Log("Not in camp, repositioning and trying again.");
            await TeleportToCampZone(token);
            for (int i = 0; i < 2; i++)
                await Click(B, 0_500, token).ConfigureAwait(false);

            await Click(A, 1_000, token).ConfigureAwait(false);

            var menucheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(MenuOffset, 2, token).ConfigureAwait(false), 0);
            Log($"test: {menucheck}");
            if (menucheck == 66)
                return;

            Log("Attempting face up");
            await TeleportToCampZone(token);
            for (int i = 0; i < 2; i++)
                await Click(B, 0_500, token).ConfigureAwait(false);
            await SetStick(LEFT, 0, 5_000, 0_500, token).ConfigureAwait(false); // reset face forward
            await ResetStick(token).ConfigureAwait(false); // reset
            for (int i = 0; i < 2; i++)
                await Click(B, 0_500, token).ConfigureAwait(false);

            await Click(A, 1_000, token).ConfigureAwait(false);

            menucheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(MenuOffset, 2, token).ConfigureAwait(false), 0);
            Log($"test: {menucheck}");
            if (menucheck == 66)
                return;

            Log("Attempting face down");
            await TeleportToCampZone(token);
            for (int i = 0; i < 2; i++)
                await Click(B, 0_500, token).ConfigureAwait(false);
            await SetStick(LEFT, 0, -5_000, 0_500, token).ConfigureAwait(false); // reset face forward
            await ResetStick(token).ConfigureAwait(false); // reset                
            for (int i = 0; i < 2; i++)
                await Click(B, 0_500, token).ConfigureAwait(false);

            await Click(A, 1_000, token).ConfigureAwait(false);

            menucheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(MenuOffset, 2, token).ConfigureAwait(false), 0);
            Log($"test: {menucheck}");
            if (menucheck == 66)
                return;
            Log("Attempting face right");
            await TeleportToCampZone(token);
            for (int i = 0; i < 2; i++)
                await Click(B, 0_500, token).ConfigureAwait(false);
            await SetStick(LEFT, 5_000, 0, 0_500, token).ConfigureAwait(false); // reset face forward
            await ResetStick(token).ConfigureAwait(false); // reset
            for (int i = 0; i < 2; i++)
                await Click(B, 0_500, token).ConfigureAwait(false);

            await Click(A, 1_000, token).ConfigureAwait(false);

            menucheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(MenuOffset, 2, token).ConfigureAwait(false), 0);
            Log($"test: {menucheck}");
            if (menucheck == 66)
                return;
            Log("Attempting face left");
            await TeleportToCampZone(token);
            for (int i = 0; i < 2; i++)
                await Click(B, 0_500, token).ConfigureAwait(false);
            await SetStick(LEFT, -5_000, 0, 0_500, token).ConfigureAwait(false); // reset face forward
            await ResetStick(token).ConfigureAwait(false); // reset
            for (int i = 0; i < 2; i++)
                await Click(B, 0_500, token).ConfigureAwait(false);

            await Click(A, 1_000, token).ConfigureAwait(false);

            menucheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(MenuOffset, 2, token).ConfigureAwait(false), 0);
            Log($"test: {menucheck}");
            if (menucheck == 66)
                return;
        }
    }

    private async Task DistortionReader(PokedexSaveData dex, CancellationToken token)
    {
        List<(PA8 match, (string, string, string) coords)> matchlist = [];
        List<string> loglist = [];
        Log($"Starting Distortion Scanner for {Settings.DistortionConditions.DistortionLocation}...");
        int tries = 1;
        ulong? lastGroup0Seed = null;
        int count = Settings.DistortionConditions.DistortionLocation switch
        {
            ArceusMap.ObsidianFieldlands => 16,
            ArceusMap.CrimsonMirelands => 25,
            ArceusMap.CobaltCoastlands or ArceusMap.CoronetHighlands => 20,
            ArceusMap.AlabasterIcelands => 24,
            _ => 0,
        };

        while (!token.IsCancellationRequested)
        {
            Log($"Scan #{tries}...");

            {
                long[] warmupOfs = Settings.DistortionConditions.DistortionLocation switch
                {
                    ArceusMap.ObsidianFieldlands => [0x42CC4D8, 0xC0, 0x1C0, 0x990 + 0 * 0x8, 0x18, 0x430, 0xC0],
                    ArceusMap.CrimsonMirelands => [0x42CC4D8, 0xC0, 0x1C0, 0xC78 + 0 * 0x8, 0x18, 0x430, 0xC0],
                    ArceusMap.CobaltCoastlands => [0x42CC4D8, 0xC0, 0x1C0, 0xCC0 + 0 * 0x8, 0x18, 0x430, 0xC0],
                    ArceusMap.CoronetHighlands => [0x42CC4D8, 0xC0, 0x1C0, 0x818 + 0 * 0x8, 0x18, 0x430, 0xC0],
                    ArceusMap.AlabasterIcelands => [0x42CC4D8, 0xC0, 0x1C0, 0x948 + 0 * 0x8, 0x18, 0x430, 0xC0],
                    _ => throw new NotImplementedException("Invalid distortion location."),
                };
                var warmupSpawnerOff = await SwitchConnection.PointerAll(warmupOfs, token).ConfigureAwait(false);
                _ = await SwitchConnection.ReadBytesAbsoluteAsync(warmupSpawnerOff, 8, token).ConfigureAwait(false);
                var warmupSpawnerOff2 = await SwitchConnection.PointerAll(warmupOfs, token).ConfigureAwait(false);
                _ = await SwitchConnection.ReadBytesAbsoluteAsync(warmupSpawnerOff2, 8, token).ConfigureAwait(false);
                var warmupSpawnerOff3 = await SwitchConnection.PointerAll(warmupOfs, token).ConfigureAwait(false);
                _ = await SwitchConnection.ReadBytesAbsoluteAsync(warmupSpawnerOff3, 8, token).ConfigureAwait(false);
            }

            ulong? previousGroupSeed = null;
            int previousIndex = -1;

            for (int i = 0; i < count; i++)
            {
                int encounter_slot_sum;
                int common_sum;
                long[] disofs;
                switch (Settings.DistortionConditions.DistortionLocation)
                {
                    case ArceusMap.ObsidianFieldlands: disofs = [0x42CC4D8, 0xC0, 0x1C0, 0x990 + i * 0x8, 0x18, 0x430, 0xC0]; encounter_slot_sum = 112; common_sum = 546; break;
                    case ArceusMap.CrimsonMirelands: disofs = [0x42CC4D8, 0xC0, 0x1C0, 0xC78 + i * 0x8, 0x18, 0x430, 0xC0]; encounter_slot_sum = 276; common_sum = 480; break;
                    case ArceusMap.CobaltCoastlands: disofs = [0x42CC4D8, 0xC0, 0x1C0, 0xCC0 + i * 0x8, 0x18, 0x430, 0xC0]; encounter_slot_sum = 163; common_sum = 529; break;
                    case ArceusMap.CoronetHighlands: disofs = [0x42CC4D8, 0xC0, 0x1C0, 0x818 + i * 0x8, 0x18, 0x430, 0xC0]; encounter_slot_sum = 382; common_sum = 382; break;
                    case ArceusMap.AlabasterIcelands: disofs = [0x42CC4D8, 0xC0, 0x1C0, 0x948 + i * 0x8, 0x18, 0x430, 0xC0]; encounter_slot_sum = 259; common_sum = 675; break;
                    default: throw new NotImplementedException("Invalid distortion location.");
                }

                var SpawnerOff = await SwitchConnection.PointerAll(disofs, token).ConfigureAwait(false);
                var GeneratorSeed = await SwitchConnection.ReadBytesAbsoluteAsync(SpawnerOff, 8, token).ConfigureAwait(false);
                var group_seed = (BitConverter.ToUInt64(GeneratorSeed, 0) - 0x82A2B175229D6A5B) & 0xFFFFFFFFFFFFFFFF;

                if (i == 0)
                {
                    const int Group0RetryAttempts = 3;
                    const int Group0RetryDelayMs = 1_000;

                    if (group_seed != 0 && lastGroup0Seed.HasValue && group_seed == lastGroup0Seed.Value)
                    {
                        Log($"[Stale check] Group 0 seed {group_seed:X16} matches the previous pass -- discarding and re-reading...");
                        bool changed = false;
                        for (int retry = 1; retry <= Group0RetryAttempts; retry++)
                        {
                            await Task.Delay(Group0RetryDelayMs, token).ConfigureAwait(false);
                            SpawnerOff = await SwitchConnection.PointerAll(disofs, token).ConfigureAwait(false);
                            GeneratorSeed = await SwitchConnection.ReadBytesAbsoluteAsync(SpawnerOff, 8, token).ConfigureAwait(false);
                            group_seed = (BitConverter.ToUInt64(GeneratorSeed, 0) - 0x82A2B175229D6A5B) & 0xFFFFFFFFFFFFFFFF;
                            if (group_seed != lastGroup0Seed.Value)
                            {
                                changed = true;
                                Log($"[Stale check] Re-read {retry} returned a fresh seed ({group_seed:X16}) -- using it.");
                                break;
                            }
                        }

                        if (!changed)
                        {
                            Log("[Stale check] Group 0 still identical after re-reads -- skipping group 0 for this pass.");
                            continue;
                        }
                    }

                    lastGroup0Seed = group_seed;
                }

                if (group_seed != 0)
                {
                    if (previousGroupSeed.HasValue && previousGroupSeed.Value == group_seed && i == previousIndex + 1)
                    {
                        await CaptureAnomalyDump(i, previousIndex, group_seed, SpawnerOff, count, token).ConfigureAwait(false);
                    }
                    previousGroupSeed = group_seed;
                    previousIndex = i;

                    if (i >= 13 && i <= 15 && Settings.DistortionConditions.DistortionLocation == ArceusMap.CrimsonMirelands)
                        encounter_slot_sum = 118;

                    var (match, shiny, logs) = ReadDistortionSeed(dex, i, group_seed, encounter_slot_sum, common_sum);
                    loglist.Add(logs);
                    string[] monlist = Settings.SpeciesToHunt.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

                    if (shiny || Settings.DistortionConditions.AnyAlpha && match.IsAlpha)
                    {
                        if (monlist.Length != 0)
                        {
                            bool huntedspecies = monlist.Contains($"{(Species)match.Species}");
                            if (!huntedspecies)
                            {
                                string url = TradeExtensions<PK9>.PokeImg(match, false, false);
                                var print = Hub.Config.StopConditions.GetAlphaPrintName(match);
                                EchoUtil.EchoEmbed("", print, url, "", false);
                                Log(logs);
                                break;
                            }
                        }

                        bool wrongGender = Settings.DistortionConditions.DesiredGender != GenderType.Any
                                           && (int)Settings.DistortionConditions.DesiredGender != match.Gender;
                        if (wrongGender)
                        {
                            string url = TradeExtensions<PK9>.PokeImg(match, false, false);
                            var print = Hub.Config.StopConditions.GetAlphaPrintName(match);
                            EchoUtil.EchoEmbed("", print, url, "", false);
                            Log(logs);
                            break;
                        }

                        matchlist.Add((match, FillDistortionCoords(i)));

                        if (Settings.DistortionConditions.AnyAlpha && match.IsAlpha)
                        {
                            if (Settings.SpecialConditions.UsePlayerInvincibleCheat)
                            {
                                // Activates invcincible trainer cheat so we don't faint from teleporting or a Pokemon attacking and infinite PP
                                await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0xD65F03C0), MainNsoBase + InvincibleTrainer1, token).ConfigureAwait(false);
                                await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0xD65F03C0), MainNsoBase + InvincibleTrainer2, token).ConfigureAwait(false);
                            }

                            Log($"Found an Alpha {(Species)match.Species}. Storing its coordinates...\nPress continue if a desired encounter to teleport to, otherwise toss to toss.\nReference the image guide if needed: https://imgur.com/a/OyBIIbR");
                            string url = TradeExtensions<PK9>.PokeImg(match, false, false);
                            var print = Hub.Config.StopConditions.GetAlphaPrintName(match);
                            EchoUtil.EchoEmbed(Hub.Config.StopConditions.MatchFoundEchoMention, print, url, "", true);
                            FillDistortionCoords(i);
                            IsWaiting = true;
                            IsWaitingConfirmation = true;
                            await Click(HOME, 1_000, token).ConfigureAwait(false);

                            while (IsWaiting && IsWaitingConfirmation)
                            {
                                if (IsWaiting && !IsWaitingConfirmation || !IsWaiting && IsWaitingConfirmation)
                                    break;
                                await Task.Delay(1_000, token).ConfigureAwait(false);
                            }
                            await Click(HOME, 1_000, token).ConfigureAwait(false);

                            if (Settings.DistortionConditions.TeleportToDistortionLocation && !IsWaitingConfirmation)
                                await TeleportToDistortionZone(token).ConfigureAwait(false);

                        }
                    }
                }
                foreach (var (match, coords) in matchlist)
                {
                    if (match.IsShiny)
                    {
                        string url = string.Empty;
                        string print = string.Empty;
                        if (Settings.DistortionConditions.ShinyAlphaOnly)
                        {
                            if (!match.IsAlpha)
                            {
                                url = TradeExtensions<PK9>.PokeImg(match, false, false);
                                print = Hub.Config.StopConditions.GetAlphaPrintName(match);
                                EchoUtil.EchoEmbed("", print, url, "", false);
                                break;
                            }
                        }
                        Log(loglist.Last());

                        url = TradeExtensions<PK9>.PokeImg(match, false, false);
                        print = Hub.Config.StopConditions.GetAlphaPrintName(match);
                        EchoUtil.EchoEmbed(Hub.Config.StopConditions.MatchFoundEchoMention, print, url, "", true);
                        Log($"\nReference the image guide if needed: https://imgur.com/a/OyBIIbR");
                        Settings.AddCompletedShinyAlphaFound();

                        if (Settings.DistortionConditions.TeleportToDistortionLocation)
                            await TeleportToDistortionZone(token, coords).ConfigureAwait(false);

                        await Click(HOME, 1_000, token).ConfigureAwait(false);
                        IsWaiting = true;
                        while (IsWaiting)
                            await Task.Delay(1_000, token).ConfigureAwait(false);

                        if (!IsWaiting)
                            await Click(HOME, 1_000, token).ConfigureAwait(false);
                    }
                }
                matchlist.Clear();
            }
            string report = string.Join("\n", loglist);
            Log(report);
            loglist.Clear();
            tries++;

            if (Settings.OutbreakConditions.CheckDistortionFirst && Settings.BotType == ArceusMode.MassiveOutbreakHunter)
                return;

            await CloseGame(Hub.Config, token).ConfigureAwait(false);
            await StartGame(Hub.Config, token).ConfigureAwait(false);
        }
    }

    private (string, string, string) FillDistortionCoords(int id) => Settings.DistortionConditions.DistortionLocation switch
    {
        ArceusMap.ObsidianFieldlands when id is <= 4 => coordinates = ("4402d333", "4206c0a0", "43296666"),
        ArceusMap.ObsidianFieldlands when id is > 4 and <= 8 => coordinates = ("43de0ccd", "41e94aea", "43f97333"),
        ArceusMap.ObsidianFieldlands when id is > 8 and <= 12 => coordinates = ("44206666", "4207a9b0", "4430b333"),
        ArceusMap.ObsidianFieldlands when id is > 12 and <= 16 => coordinates = ("43154ccd", "42094701", "443e3333"),

        ArceusMap.CrimsonMirelands when id is <= 4 => coordinates = ("44553333", "42167c7d", "4433e000"),
        ArceusMap.CrimsonMirelands when id is > 4 and <= 8 => coordinates = ("43d9d99a", "4215872e", "445de666"),
        ArceusMap.CrimsonMirelands when id is > 8 and <= 12 => coordinates = ("44374666", "4214dc82", "4449eccd"),
        ArceusMap.CrimsonMirelands when id is > 12 and <= 16 => coordinates = ("444b8000", "42876336", "43e72666"),
        ArceusMap.CrimsonMirelands when id is > 16 and <= 20 => coordinates = ("444b8000", "42876336", "43e72666"),
        ArceusMap.CrimsonMirelands when id is > 20 and <= 24 => coordinates = ("438b3333", "4226454b", "44200666"),

        ArceusMap.CobaltCoastlands when id is <= 4 => coordinates = ("4367b333", "41da6666", "44014ccd"),
        ArceusMap.CobaltCoastlands when id is > 4 and <= 8 => coordinates = ("4373cccd", "41df9edd", "4439999a"),
        ArceusMap.CobaltCoastlands when id is > 8 and <= 12 => coordinates = ("441866e1", "41eba8a7", "445d8480"),
        ArceusMap.CobaltCoastlands when id is > 12 and <= 16 => coordinates = ("438ac000", "421cc140", "432e4ccd"),
        ArceusMap.CobaltCoastlands when id is > 16 and <= 20 => coordinates = ("43a12666", "4291b53c", "43b02666"),

        ArceusMap.CoronetHighlands when id is <= 4 => coordinates = ("44166000", "428c2f78", "442cd333"),
        ArceusMap.CoronetHighlands when id is > 4 and <= 8 => coordinates = ("44048002", "42ac8fe6", "444cb99a"),
        ArceusMap.CoronetHighlands when id is > 8 and <= 12 => coordinates = ("43e7aa3d", "430248a7", "43f90666"),
        ArceusMap.CoronetHighlands when id is > 12 and <= 16 => coordinates = ("439a41ec", "430803fe", "44159ba6"),
        ArceusMap.CoronetHighlands when id is > 16 and <= 20 => coordinates = ("43426666", "42eb01e5", "44211333"),

        ArceusMap.AlabasterIcelands when id is <= 4 => coordinates = ("4427f333", "4210645d", "4405c000"),
        ArceusMap.AlabasterIcelands when id is > 4 and <= 8 => coordinates = ("43e8599a", "420ca920", "43f7c000"),
        ArceusMap.AlabasterIcelands when id is > 8 and <= 12 => coordinates = ("440ea000", "41fe8750", "4435c000"),
        ArceusMap.AlabasterIcelands when id is > 12 and <= 16 => coordinates = ("43b6b333", "42073571", "4422999a"),
        ArceusMap.AlabasterIcelands when id is > 16 and <= 20 => coordinates = ("444f999a", "41f13987", "43bf6666"),
        ArceusMap.AlabasterIcelands when id is > 20 and <= 24 => coordinates = ("433ecccd", "4206563e", "4428199a"),
        _ => throw new NotImplementedException("Invalid location coordinates."),

    };
    private string GetDistortionSpeciesLocation(int id) => Settings.DistortionConditions.DistortionLocation switch
    {
        ArceusMap.ObsidianFieldlands when id is <= 4 => "Horseshoe Plains",
        ArceusMap.ObsidianFieldlands when id is > 4 and <= 8 => "Windswept Run",
        ArceusMap.ObsidianFieldlands when id is > 8 and <= 12 => "Nature's Pantry",
        ArceusMap.ObsidianFieldlands when id is > 12 and <= 16 => "Sandgem Flats",

        ArceusMap.CrimsonMirelands when id is <= 4 => "Droning Meadow",
        ArceusMap.CrimsonMirelands when id is > 4 and <= 8 => "Holm of Trials",
        ArceusMap.CrimsonMirelands when id is > 8 and <= 12 => "Location Unknown",
        ArceusMap.CrimsonMirelands when id is > 12 and <= 16 => "North of Ursa's Ring",
        ArceusMap.CrimsonMirelands when id is > 16 and <= 20 => "To the right of Bolderoll Slope",
        ArceusMap.CrimsonMirelands when id is > 20 and <= 24 => "Gapejaw Bog",

        ArceusMap.CobaltCoastlands when id is <= 4 => "Below Windbreak Stand",
        ArceusMap.CobaltCoastlands when id is > 4 and <= 8 => "Right of Crossing Slope",
        ArceusMap.CobaltCoastlands when id is > 8 and <= 12 => "Right of Bather's Lagoon",
        ArceusMap.CobaltCoastlands when id is > 12 and <= 16 => "Left of Islespy Shore",
        ArceusMap.CobaltCoastlands when id is > 16 and <= 20 => "Right of Windbreak Stand",

        ArceusMap.CoronetHighlands when id is <= 4 => "Between Sonorous Path and Celestica Trail",
        ArceusMap.CoronetHighlands when id is > 4 and <= 8 => "Ancient Quarry",
        ArceusMap.CoronetHighlands when id is > 8 and <= 12 => "North of Primeval Grotto",
        ArceusMap.CoronetHighlands when id is > 12 and <= 16 => "South of Sacred Plaza",
        ArceusMap.CoronetHighlands when id is > 16 and <= 20 => "Between Boulderoll Ravine and Stonetooth Rows",

        ArceusMap.AlabasterIcelands when id is <= 4 => "Between and to the right of Bonechill Wastes and Avalugg's Legacy",
        ArceusMap.AlabasterIcelands when id is > 4 and <= 8 => "Little left of Avalugg's Legacy",
        ArceusMap.AlabasterIcelands when id is > 8 and <= 12 => "Between Bonechill Wastes and Whiteout Valley",
        ArceusMap.AlabasterIcelands when id is > 12 and <= 16 => "To the right of Arena's Approach",
        ArceusMap.AlabasterIcelands when id is > 16 and <= 20 => "Heart's Crag",
        ArceusMap.AlabasterIcelands when id is > 20 and <= 24 => "North of Avalanche Slopes",
        _ => throw new NotImplementedException("Invalid location ID."),
    };

    private PA8 GetCommonDistortionSpecies(double encslot) => Settings.DistortionConditions.DistortionLocation switch
    {
        ArceusMap.ObsidianFieldlands when encslot is < 75 => new() { Species = (int)Species.Onix },
        ArceusMap.ObsidianFieldlands when encslot is > 75 and < 76 => new() { Species = (int)Species.Onix, IsAlpha = true },
        ArceusMap.ObsidianFieldlands when encslot is > 76 and < 86 => new() { Species = (int)Species.Steelix },
        ArceusMap.ObsidianFieldlands when encslot is > 86 and < 87 => new() { Species = (int)Species.Steelix, IsAlpha = true },
        ArceusMap.ObsidianFieldlands when encslot is > 87 and < 187 => new() { Species = (int)Species.Haunter },
        ArceusMap.ObsidianFieldlands when encslot is > 187 and < 188 => new() { Species = (int)Species.Haunter, IsAlpha = true },
        ArceusMap.ObsidianFieldlands when encslot is > 188 and < 198 => new() { Species = (int)Species.Gengar },
        ArceusMap.ObsidianFieldlands when encslot is > 198 and < 199 => new() { Species = (int)Species.Gengar, IsAlpha = true },
        ArceusMap.ObsidianFieldlands when encslot is > 199 and < 299 => new() { Species = (int)Species.Lickitung },
        ArceusMap.ObsidianFieldlands when encslot is > 299 and < 300 => new() { Species = (int)Species.Lickitung, IsAlpha = true },
        ArceusMap.ObsidianFieldlands when encslot is > 300 and < 350 => new() { Species = (int)Species.Lickilicky },
        ArceusMap.ObsidianFieldlands when encslot is > 350 and < 351 => new() { Species = (int)Species.Lickilicky, IsAlpha = true },
        ArceusMap.ObsidianFieldlands when encslot is > 351 and < 451 => new() { Species = (int)Species.Ursaring },
        ArceusMap.ObsidianFieldlands when encslot is > 451 and < 452 => new() { Species = (int)Species.Ursaring, IsAlpha = true },
        ArceusMap.ObsidianFieldlands when encslot is > 452 and < 472 => new() { Species = (int)Species.Toxicroak },
        ArceusMap.ObsidianFieldlands when encslot is > 472 and < 473 => new() { Species = (int)Species.Toxicroak, IsAlpha = true },
        ArceusMap.ObsidianFieldlands when encslot is > 473 and < 523 => new() { Species = (int)Species.Eevee },
        ArceusMap.ObsidianFieldlands when encslot is > 523 and < 524 => new() { Species = (int)Species.Eevee, IsAlpha = true },
        ArceusMap.ObsidianFieldlands when encslot is > 524 and < 534 => new() { Species = (int)Species.Leafeon },
        ArceusMap.ObsidianFieldlands when encslot is > 534 and < 535 => new() { Species = (int)Species.Leafeon, IsAlpha = true },
        ArceusMap.ObsidianFieldlands when encslot is > 535 and < 545 => new() { Species = (int)Species.Sylveon },
        ArceusMap.ObsidianFieldlands when encslot is >= 545 => new() { Species = (int)Species.Sylveon, IsAlpha = true },

        ArceusMap.CrimsonMirelands when encslot is < 100 => new() { Species = (int)Species.Floatzel },
        ArceusMap.CrimsonMirelands when encslot is > 100 and < 101 => new() { Species = (int)Species.Floatzel, IsAlpha = true },
        ArceusMap.CrimsonMirelands when encslot is > 101 and < 111 => new() { Species = (int)Species.Snorlax },
        ArceusMap.CrimsonMirelands when encslot is > 111 and < 112 => new() { Species = (int)Species.Snorlax, IsAlpha = true },
        ArceusMap.CrimsonMirelands when encslot is > 112 and < 212 => new() { Species = (int)Species.Drifblim },
        ArceusMap.CrimsonMirelands when encslot is > 212 and < 213 => new() { Species = (int)Species.Drifblim, IsAlpha = true },
        ArceusMap.CrimsonMirelands when encslot is > 213 and < 233 => new() { Species = (int)Species.Lopunny },
        ArceusMap.CrimsonMirelands when encslot is > 233 and < 234 => new() { Species = (int)Species.Lopunny, IsAlpha = true },
        ArceusMap.CrimsonMirelands when encslot is > 234 and < 334 => new() { Species = (int)Species.Luxio },
        ArceusMap.CrimsonMirelands when encslot is > 334 and < 335 => new() { Species = (int)Species.Luxio, IsAlpha = true },
        ArceusMap.CrimsonMirelands when encslot is > 335 and < 375 => new() { Species = (int)Species.Luxray },
        ArceusMap.CrimsonMirelands when encslot is > 375 and < 376 => new() { Species = (int)Species.Luxray, IsAlpha = true },
        ArceusMap.CrimsonMirelands when encslot is > 376 and < 406 => new() { Species = (int)Species.Heracross },
        ArceusMap.CrimsonMirelands when encslot is > 406 and < 407 => new() { Species = (int)Species.Heracross, IsAlpha = true },
        ArceusMap.CrimsonMirelands when encslot is > 407 and < 457 => new() { Species = (int)Species.Eevee },
        ArceusMap.CrimsonMirelands when encslot is > 457 and < 458 => new() { Species = (int)Species.Eevee, IsAlpha = true },
        ArceusMap.CrimsonMirelands when encslot is > 458 and < 468 => new() { Species = (int)Species.Umbreon },
        ArceusMap.CrimsonMirelands when encslot is > 468 and < 469 => new() { Species = (int)Species.Umbreon, IsAlpha = true },
        ArceusMap.CrimsonMirelands when encslot is > 469 and < 479 => new() { Species = (int)Species.Flareon },
        ArceusMap.CrimsonMirelands when encslot is >= 479 => new() { Species = (int)Species.Flareon, IsAlpha = true },

        ArceusMap.CobaltCoastlands when encslot is < 100 => new() { Species = (int)Species.Kadabra },
        ArceusMap.CobaltCoastlands when encslot is > 100 and < 101 => new() { Species = (int)Species.Kadabra, IsAlpha = true },
        ArceusMap.CobaltCoastlands when encslot is > 101 and < 111 => new() { Species = (int)Species.Alakazam },
        ArceusMap.CobaltCoastlands when encslot is > 111 and < 112 => new() { Species = (int)Species.Alakazam, IsAlpha = true },
        ArceusMap.CobaltCoastlands when encslot is > 112 and < 212 => new() { Species = (int)Species.Rhydon },
        ArceusMap.CobaltCoastlands when encslot is > 212 and < 213 => new() { Species = (int)Species.Rhydon, IsAlpha = true },
        ArceusMap.CobaltCoastlands when encslot is > 213 and < 223 => new() { Species = (int)Species.Rhyperior },
        ArceusMap.CobaltCoastlands when encslot is > 223 and < 224 => new() { Species = (int)Species.Rhyperior, IsAlpha = true },
        ArceusMap.CobaltCoastlands when encslot is > 224 and < 324 => new() { Species = (int)Species.Skuntank },
        ArceusMap.CobaltCoastlands when encslot is > 324 and < 325 => new() { Species = (int)Species.Skuntank, IsAlpha = true },
        ArceusMap.CobaltCoastlands when encslot is > 325 and < 425 => new() { Species = (int)Species.Carnivine },
        ArceusMap.CobaltCoastlands when encslot is > 425 and < 426 => new() { Species = (int)Species.Carnivine, IsAlpha = true },
        ArceusMap.CobaltCoastlands when encslot is > 426 and < 526 => new() { Species = (int)Species.MrMime },
        ArceusMap.CobaltCoastlands when encslot is > 526 and < 527 => new() { Species = (int)Species.MrMime, IsAlpha = true },
        ArceusMap.CobaltCoastlands when encslot is > 527 and < 557 => new() { Species = (int)Species.Eevee },
        ArceusMap.CobaltCoastlands when encslot is > 557 and < 558 => new() { Species = (int)Species.Eevee, IsAlpha = true },
        ArceusMap.CobaltCoastlands when encslot is > 558 and < 568 => new() { Species = (int)Species.Vaporeon },
        ArceusMap.CobaltCoastlands when encslot is > 568 and < 569 => new() { Species = (int)Species.Vaporeon, IsAlpha = true },
        ArceusMap.CobaltCoastlands when encslot is > 569 and < 579 => new() { Species = (int)Species.Flareon },
        ArceusMap.CobaltCoastlands when encslot is >= 579 => new() { Species = (int)Species.Flareon, IsAlpha = true },

        ArceusMap.CoronetHighlands when encslot is < 100 => new() { Species = (int)Species.Magmar },
        ArceusMap.CoronetHighlands when encslot is > 100 and < 101 => new() { Species = (int)Species.Magmar, IsAlpha = true },
        ArceusMap.CoronetHighlands when encslot is > 101 and < 201 => new() { Species = (int)Species.Dusclops },
        ArceusMap.CoronetHighlands when encslot is > 201 and < 202 => new() { Species = (int)Species.Dusclops, IsAlpha = true },
        ArceusMap.CoronetHighlands when encslot is > 202 and < 212 => new() { Species = (int)Species.Dusknoir },
        ArceusMap.CoronetHighlands when encslot is > 212 and < 213 => new() { Species = (int)Species.Dusknoir, IsAlpha = true },
        ArceusMap.CoronetHighlands when encslot is > 213 and < 313 => new() { Species = (int)Species.Octillery },
        ArceusMap.CoronetHighlands when encslot is > 313 and < 314 => new() { Species = (int)Species.Octillery, IsAlpha = true },
        ArceusMap.CoronetHighlands when encslot is > 314 and < 414 => new() { Species = (int)Species.Drapion },
        ArceusMap.CoronetHighlands when encslot is > 414 and < 415 => new() { Species = (int)Species.Drapion, IsAlpha = true },
        ArceusMap.CoronetHighlands when encslot is > 415 and < 455 => new() { Species = (int)Species.Ambipom },
        ArceusMap.CoronetHighlands when encslot is > 455 and < 456 => new() { Species = (int)Species.Ambipom, IsAlpha = true },
        ArceusMap.CoronetHighlands when encslot is > 456 and < 506 => new() { Species = (int)Species.Eevee },
        ArceusMap.CoronetHighlands when encslot is > 506 and < 507 => new() { Species = (int)Species.Eevee, IsAlpha = true },
        ArceusMap.CoronetHighlands when encslot is > 507 and < 517 => new() { Species = (int)Species.Jolteon },
        ArceusMap.CoronetHighlands when encslot is > 517 and < 518 => new() { Species = (int)Species.Jolteon, IsAlpha = true },
        ArceusMap.CoronetHighlands when encslot is > 518 and < 528 => new() { Species = (int)Species.Sylveon },
        ArceusMap.CoronetHighlands when encslot is >= 528 => new() { Species = (int)Species.Sylveon, IsAlpha = true },

        ArceusMap.AlabasterIcelands when encslot is < 100 => new() { Species = (int)Species.Electabuzz },
        ArceusMap.AlabasterIcelands when encslot is > 100 and < 102 => new() { Species = (int)Species.Electabuzz, IsAlpha = true },
        ArceusMap.AlabasterIcelands when encslot is > 102 and < 112 => new() { Species = (int)Species.Electivire },
        ArceusMap.AlabasterIcelands when encslot is > 112 and < 113 => new() { Species = (int)Species.Electivire, IsAlpha = true },
        ArceusMap.AlabasterIcelands when encslot is > 113 and < 173 => new() { Species = (int)Species.Pikachu },
        ArceusMap.AlabasterIcelands when encslot is > 173 and < 175 => new() { Species = (int)Species.Pikachu, IsAlpha = true },
        ArceusMap.AlabasterIcelands when encslot is > 175 and < 195 => new() { Species = (int)Species.Raichu },
        ArceusMap.AlabasterIcelands when encslot is > 195 and < 196 => new() { Species = (int)Species.Raichu, IsAlpha = true },
        ArceusMap.AlabasterIcelands when encslot is > 196 and < 296 => new() { Species = (int)Species.Sealeo },
        ArceusMap.AlabasterIcelands when encslot is > 296 and < 298 => new() { Species = (int)Species.Sealeo, IsAlpha = true },
        ArceusMap.AlabasterIcelands when encslot is > 298 and < 378 => new() { Species = (int)Species.Walrein },
        ArceusMap.AlabasterIcelands when encslot is > 378 and < 379 => new() { Species = (int)Species.Walrein, IsAlpha = true },
        ArceusMap.AlabasterIcelands when encslot is > 379 and < 459 => new() { Species = (int)Species.Rapidash },
        ArceusMap.AlabasterIcelands when encslot is > 459 and < 460 => new() { Species = (int)Species.Rapidash, IsAlpha = true },
        ArceusMap.AlabasterIcelands when encslot is > 460 and < 500 => new() { Species = (int)Species.Tangrowth },
        ArceusMap.AlabasterIcelands when encslot is > 500 and < 501 => new() { Species = (int)Species.Tangrowth, IsAlpha = true },
        ArceusMap.AlabasterIcelands when encslot is > 501 and < 601 => new() { Species = (int)Species.Scyther },
        ArceusMap.AlabasterIcelands when encslot is > 601 and < 602 => new() { Species = (int)Species.Scyther, IsAlpha = true },
        ArceusMap.AlabasterIcelands when encslot is > 602 and < 652 => new() { Species = (int)Species.Eevee },
        ArceusMap.AlabasterIcelands when encslot is > 652 and < 653 => new() { Species = (int)Species.Eevee, IsAlpha = true },
        ArceusMap.AlabasterIcelands when encslot is > 653 and < 663 => new() { Species = (int)Species.Glaceon },
        ArceusMap.AlabasterIcelands when encslot is > 663 and < 664 => new() { Species = (int)Species.Glaceon, IsAlpha = true },
        ArceusMap.AlabasterIcelands when encslot is > 664 and < 674 => new() { Species = (int)Species.Espeon },
        ArceusMap.AlabasterIcelands when encslot is >= 674 => new() { Species = (int)Species.Espeon, IsAlpha = true },
        _ => throw new NotImplementedException("Not a valid encounter slot."),
    };

    private PA8 GetDistortionSpecies(double encslot) => Settings.DistortionConditions.DistortionLocation switch
    {
        ArceusMap.ObsidianFieldlands when encslot is <= 100 => new() { Species = (int)Species.Sneasel },
        ArceusMap.ObsidianFieldlands when encslot is > 100 and < 101 => new() { Species = (int)Species.Sneasel, IsAlpha = true },
        ArceusMap.ObsidianFieldlands when encslot is > 101 and < 111 => new() { Species = (int)Species.Weavile },
        ArceusMap.ObsidianFieldlands when encslot is > 111 and < 112 => new() { Species = (int)Species.Weavile, IsAlpha = true },

        ArceusMap.CrimsonMirelands when encslot is <= 100 => new() { Species = (int)Species.Porygon },
        ArceusMap.CrimsonMirelands when encslot is > 100 and < 101 => new() { Species = (int)Species.Porygon, IsAlpha = true },
        ArceusMap.CrimsonMirelands when encslot is > 101 and < 111 => new() { Species = (int)Species.Porygon2 },
        ArceusMap.CrimsonMirelands when encslot is > 111 and < 112 => new() { Species = (int)Species.Porygon2, IsAlpha = true },
        ArceusMap.CrimsonMirelands when encslot is > 112 and < 117 => new() { Species = (int)Species.PorygonZ },
        ArceusMap.CrimsonMirelands when encslot is > 117 and < 118 => new() { Species = (int)Species.PorygonZ, IsAlpha = true },
        ArceusMap.CrimsonMirelands when encslot is > 118 and < 218 => new() { Species = (int)Species.Cyndaquil },
        ArceusMap.CrimsonMirelands when encslot is > 218 and < 219 => new() { Species = (int)Species.Cyndaquil, IsAlpha = true },
        ArceusMap.CrimsonMirelands when encslot is > 219 and < 269 => new() { Species = (int)Species.Quilava },
        ArceusMap.CrimsonMirelands when encslot is > 269 and < 270 => new() { Species = (int)Species.Quilava, IsAlpha = true },
        ArceusMap.CrimsonMirelands when encslot is > 270 and < 275 => new() { Species = (int)Species.Typhlosion },
        ArceusMap.CrimsonMirelands when encslot is >= 275 => new() { Species = (int)Species.Typhlosion, IsAlpha = true },

        ArceusMap.CobaltCoastlands when encslot is <= 100 => new() { Species = (int)Species.Magnemite },
        ArceusMap.CobaltCoastlands when encslot is > 100 and < 101 => new() { Species = (int)Species.Magnemite, IsAlpha = true },
        ArceusMap.CobaltCoastlands when encslot is > 101 and < 151 => new() { Species = (int)Species.Magneton },
        ArceusMap.CobaltCoastlands when encslot is > 151 and < 152 => new() { Species = (int)Species.Magneton, IsAlpha = true },
        ArceusMap.CobaltCoastlands when encslot is > 152 and < 162 => new() { Species = (int)Species.Magnezone },
        ArceusMap.CobaltCoastlands when encslot is >= 162 => new() { Species = (int)Species.Magnezone, IsAlpha = true },

        ArceusMap.CoronetHighlands when encslot is <= 100 => new() { Species = (int)Species.Cranidos },
        ArceusMap.CoronetHighlands when encslot is > 100 and < 101 => new() { Species = (int)Species.Cranidos, IsAlpha = true },
        ArceusMap.CoronetHighlands when encslot is > 101 and < 111 => new() { Species = (int)Species.Rampardos },
        ArceusMap.CoronetHighlands when encslot is > 111 and < 112 => new() { Species = (int)Species.Rampardos, IsAlpha = true },
        ArceusMap.CoronetHighlands when encslot is > 112 and < 212 => new() { Species = (int)Species.Shieldon },
        ArceusMap.CoronetHighlands when encslot is > 212 and < 213 => new() { Species = (int)Species.Shieldon, IsAlpha = true },
        ArceusMap.CoronetHighlands when encslot is > 213 and < 223 => new() { Species = (int)Species.Bastiodon },
        ArceusMap.CoronetHighlands when encslot is > 223 and < 224 => new() { Species = (int)Species.Bastiodon, IsAlpha = true },
        ArceusMap.CoronetHighlands when encslot is > 224 and < 324 => new() { Species = (int)Species.Rowlet },
        ArceusMap.CoronetHighlands when encslot is > 324 and < 325 => new() { Species = (int)Species.Rowlet, IsAlpha = true },
        ArceusMap.CoronetHighlands when encslot is > 325 and < 375 => new() { Species = (int)Species.Dartrix },
        ArceusMap.CoronetHighlands when encslot is > 375 and < 376 => new() { Species = (int)Species.Dartrix, IsAlpha = true },
        ArceusMap.CoronetHighlands when encslot is > 376 and < 381 => new() { Species = (int)Species.Decidueye },
        ArceusMap.CoronetHighlands when encslot is > 381 and < 382 => new() { Species = (int)Species.Decidueye, IsAlpha = true },

        ArceusMap.AlabasterIcelands when encslot is <= 100 => new() { Species = (int)Species.Scizor },
        ArceusMap.AlabasterIcelands when encslot is > 100 and < 101 => new() { Species = (int)Species.Scizor, IsAlpha = true },
        ArceusMap.AlabasterIcelands when encslot is > 101 and < 201 => new() { Species = (int)Species.Oshawott },
        ArceusMap.AlabasterIcelands when encslot is > 201 and < 202 => new() { Species = (int)Species.Oshawott, IsAlpha = true },
        ArceusMap.AlabasterIcelands when encslot is > 202 and < 252 => new() { Species = (int)Species.Dewott },
        ArceusMap.AlabasterIcelands when encslot is > 252 and < 253 => new() { Species = (int)Species.Dewott, IsAlpha = true },
        ArceusMap.AlabasterIcelands when encslot is > 253 and < 258 => new() { Species = (int)Species.Samurott },
        ArceusMap.AlabasterIcelands when encslot is >= 258 => new() { Species = (int)Species.Samurott, IsAlpha = true },
        _ => throw new NotImplementedException("Not a valid encounter slot."),
    };

    private async Task DistortionSpammer(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Log("Whipping up a distortion...");
            // Activates distortions
            await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0x5280010A), MainNsoBase + ActivateDistortion, token).ConfigureAwait(false);
            await Task.Delay(0_500, token).ConfigureAwait(false);
            await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0x7100052A), MainNsoBase + ActivateDistortion, token).ConfigureAwait(false);
            var delta = DateTime.Now;
            var cd = TimeSpan.FromMinutes(Settings.DistortionConditions.WaitTimeDistortion);
            Log($"Waiting {Settings.DistortionConditions.WaitTimeDistortion} minutes then starting the next one...");
            while (DateTime.Now - delta < cd && !token.IsCancellationRequested)
            {
                await Task.Delay(5_000, token).ConfigureAwait(false);
                Log($"Time Remaining: {cd - (DateTime.Now - delta)}");
            }

            await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0x5280010A), MainNsoBase + ActivateDistortion, token).ConfigureAwait(false);
            await Task.Delay(0_500, token).ConfigureAwait(false);
            await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0x7100052A), MainNsoBase + ActivateDistortion, token).ConfigureAwait(false);
            await Task.Delay(5_000, token).ConfigureAwait(false);
        }
    }

    private async Task TimeSeedAdvancer(CancellationToken token)
    {
        int success = 0;
        int heal = 0;
        string[] coords = [Settings.SpecialConditions.SpawnZoneX, Settings.SpecialConditions.SpawnZoneY, Settings.SpecialConditions.SpawnZoneZ];
        for (int a = 0; a < coords.Length; a++)
        {
            if (string.IsNullOrEmpty(coords[a]))
            {
                Log($"One of your coordinates is empty, please fill it accordingly!");
                return;
            }
        }
        OverworldOffset = await SwitchConnection.PointerAll(Offsets.OverworldPointer, token).ConfigureAwait(false);
        if (Settings.SpecialConditions.UsePlayerInvincibleCheat)
        {
            // Activates invcincible trainer cheat so we don't faint from teleporting or a Pokemon attacking and infinite PP
            await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0xD65F03C0), MainNsoBase + InvincibleTrainer1, token).ConfigureAwait(false);
            await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0xD65F03C0), MainNsoBase + InvincibleTrainer2, token).ConfigureAwait(false);

            await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0xD28008AA), MainNsoBase + InfPP1, token).ConfigureAwait(false);
            await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0xD28008A9), MainNsoBase + InfPP2, token).ConfigureAwait(false);
        }

        GetDefaultCampCoords();
        while (!token.IsCancellationRequested)
        {
            string alpha = string.Empty;
            for (int adv = 0; adv < Settings.AlphaScanConditions.Advances; adv++)
            {
                Log($"Advancing {Settings.AlphaScanConditions.Advances - adv} times...");
                await Click(B, 1_000, token).ConfigureAwait(false);// Random B incase of button miss
                await Click(A, 1_000, token).ConfigureAwait(false);
                await TimeTest(token).ConfigureAwait(false);
                await Task.Delay(2_000, token).ConfigureAwait(false);
                if (Settings.AlphaScanConditions.SpawnIsStaticAlpha)
                {
                    for (int i = 0; i < 2; i++)
                    {
                        await TimeTest(token).ConfigureAwait(false);
                        await Task.Delay(2_000, token).ConfigureAwait(false);
                    }
                }
                await TeleportToSpawnZone(token).ConfigureAwait(false);
                Log("Trying to enter battle!");

                await PressAndHold(ZL, 0_800, 0, token).ConfigureAwait(false);
                await Click(ZR, 1_000, token).ConfigureAwait(false);
                await ResetStick(token).ConfigureAwait(false);
                var overworldcheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(OverworldOffset, 2, token).ConfigureAwait(false), 0);
                if (overworldcheck == 1)
                {
                    Log("Not in battle, trying again!");
                    int tries = 0;
                    while (overworldcheck == 1)
                    {
                        if (tries == 3)
                        {
                            Log("Tried 3 times, is the encounter present? Changing time to try again.");
                            await TimeTest(token).ConfigureAwait(false);
                            await TeleportToSpawnZone(token).ConfigureAwait(false);
                            adv--;
                            break;
                        }
                        await PressAndHold(ZL, 0_800, 0, token).ConfigureAwait(false);
                        await Click(ZR, 1_000, token).ConfigureAwait(false);
                        await ResetStick(token).ConfigureAwait(false); // reset
                        overworldcheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(OverworldOffset, 2, token).ConfigureAwait(false), 0);
                        if (overworldcheck != 1)
                            break;

                        await SetStick(LEFT, 0, -5_000, 0_500, token).ConfigureAwait(false); // turn around check encounter
                        await ResetStick(token).ConfigureAwait(false); // reset
                        await Click(ZL, 1_000, token).ConfigureAwait(false);
                        overworldcheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(OverworldOffset, 2, token).ConfigureAwait(false), 0);
                        if (overworldcheck != 1)
                            break;

                        await PressAndHold(ZL, 0_800, 0, token).ConfigureAwait(false);
                        await Click(ZR, 1_000, token).ConfigureAwait(false);
                        await ResetStick(token).ConfigureAwait(false); // reset
                        overworldcheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(OverworldOffset, 2, token).ConfigureAwait(false), 0);
                        if (overworldcheck != 1)
                            break;

                        await SetStick(LEFT, 0, 5_000, 0_500, token).ConfigureAwait(false); // turn around check encounter
                        await ResetStick(token).ConfigureAwait(false); // reset
                        await Click(ZL, 1_000, token).ConfigureAwait(false);
                        tries++;
                    }
                }
                if (overworldcheck == 0)
                {
                    ulong ofs = await NewParsePointer(WildPokemonPtrLA, token).ConfigureAwait(false);
                    var pk = await ReadPokemon(ofs, 0x168, token).ConfigureAwait(false);
                    if (pk.Species > 0 && pk.Species < (int)Species.MAX_COUNT)
                    {
                        success++;
                        heal++;
                        var print = Hub.Config.StopConditions.GetAlphaPrintName(pk);
                        if (pk.IsAlpha)
                            alpha = "Alpha - ";
                        if (pk.IsShiny)
                        {
                            Log($"In battle with {print}!");
                            string url = TradeExtensions<PK9>.PokeImg(pk, false, false);
                            EchoUtil.EchoEmbed(Hub.Config.StopConditions.MatchFoundEchoMention, print, url, "", true);
                            Settings.AddCompletedShinyAlphaFound();

                            if (DumpSetting.Dump && !string.IsNullOrEmpty(DumpSetting.DumpFolder))
                                DumpPokemon(DumpSetting.DumpFolder, "advances", pk);

                            return;
                        }
                        Log($"Mashing A to knockout {alpha}{(Species)pk.Species}!");
                        while (overworldcheck != 1)
                        {
                            overworldcheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(OverworldOffset, 2, token).ConfigureAwait(false), 0);
                            pk = await ReadPokemon(ofs, 0x168, token).ConfigureAwait(false);
                            for (int i = 0; i < 3; i++)
                                await Click(A, 0_500, token).ConfigureAwait(false);

                            if (overworldcheck == 1 || pk.Species <= 0 || pk.Species >= (int)Species.MAX_COUNT)
                                break;
                        }
                        Log($"Defeated {alpha}{(Species)pk.Species}! Returning to spawn point.");
                        alpha = string.Empty;
                    }
                }
                if (adv == Settings.AlphaScanConditions.Advances)
                    return;
                if (heal == 3 && Settings.AlphaScanConditions.HealInCamp)
                {
                    Log("Returning to camp to heal our party!");
                    await TeleportToCampZone(token).ConfigureAwait(false);
                    await Click(A, 0_800, token).ConfigureAwait(false);
                    var menucheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(MenuOffset, 2, token).ConfigureAwait(false), 0);
                    //Log($"Menu check: {menucheck}");
                    if (menucheck == 42)
                        await Click(A, 0_800, token).ConfigureAwait(false);
                    while (menucheck == 4000 || menucheck == 0000)
                    {
                        Log("Wrong menu opened? Backing out now and trying to reposition.");
                        await Click(B, 1_500, token).ConfigureAwait(false);
                        await Reposition(token).ConfigureAwait(false);
                        await Click(B, 1_500, token).ConfigureAwait(false);
                        menucheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(MenuOffset, 2, token).ConfigureAwait(false), 0);
                        if (menucheck == 4200)
                            break;
                    }
                    await Click(A, 1_000, token).ConfigureAwait(false);
                    Log("Resting for a little while!");
                    await Click(A, 12_000, token).ConfigureAwait(false);
                    await Click(A, 1_000, token).ConfigureAwait(false);
                    await TeleportToSpawnZone(token).ConfigureAwait(false);

                }
                if (success == 50)
                {
                    Log("Saving in case of a game crash!");
                    await ArceusSaveGame(token).ConfigureAwait(false);
                    success = 0;
                }
            }
        }
        Log("Advances reached! Shiny should be on the next respawn! Refer to the live map if unsure of frame!");
        await Click(HOME, 1_000, token).ConfigureAwait(false);
    }
    private async Task SeedAdvancer(CancellationToken token)
    {
        string[] coords = [Settings.SpecialConditions.CampZoneX, Settings.SpecialConditions.CampZoneY, Settings.SpecialConditions.CampZoneZ, Settings.SpecialConditions.SpawnZoneX, Settings.SpecialConditions.SpawnZoneY, Settings.SpecialConditions.SpawnZoneZ];
        for (int a = 0; a < coords.Length; a++)
        {
            if (string.IsNullOrEmpty(coords[a]))
            {
                Log($"One of your coordinates is empty, please fill it accordingly!");
                return;
            }
        }
        OverworldOffset = await SwitchConnection.PointerAll(Offsets.OverworldPointer, token).ConfigureAwait(false);
        if (Settings.SpecialConditions.UsePlayerInvincibleCheat)
        {
            // Activates invcincible trainer cheat so we don't faint from teleporting or a Pokemon attacking
            await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0xD65F03C0), MainNsoBase + InvincibleTrainer1, token).ConfigureAwait(false);//invi
            await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0xD65F03C0), MainNsoBase + InvincibleTrainer2, token).ConfigureAwait(false);//invi
        }

        while (!token.IsCancellationRequested)
        {
            GetDefaultCampCoords();
            string alpha = string.Empty;
            for (int adv = 0; adv < Settings.AlphaScanConditions.Advances; adv++)
            {
                Log($"Advancing {Settings.AlphaScanConditions.Advances - adv} times...");
                await Click(B, 1_000, token).ConfigureAwait(false);// Random B incase of button miss
                await Click(A, 1_000, token).ConfigureAwait(false);
                var menucheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(MenuOffset, 2, token).ConfigureAwait(false), 0);
                Log($"test: {menucheck}");
                if (menucheck == 66)
                    await Click(A, 0_800, token).ConfigureAwait(false);
                while (menucheck == 64 || menucheck == 0)
                {
                    Log("Wrong menu opened? Backing out now and trying to reposition.");
                    await Click(B, 1_500, token).ConfigureAwait(false);
                    await Reposition(token).ConfigureAwait(false);
                    await Click(B, 1_500, token).ConfigureAwait(false);
                    menucheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(MenuOffset, 2, token).ConfigureAwait(false), 0);
                    if (menucheck == 66)
                        break;
                }
                if (adv % 2 == 0)
                    await Click(DDOWN, 0_500, token).ConfigureAwait(false);
                if (adv % 2 != 0)
                {
                    await Click(DUP, 0_500, token).ConfigureAwait(false);
                    await Click(DUP, 0_500, token).ConfigureAwait(false);
                }
                Log("Resting...");
                await Click(A, 12_000, token).ConfigureAwait(false);
                Log("Rested once!");
                await Click(A, 1_000, token).ConfigureAwait(false);
                if (Settings.AlphaScanConditions.SpawnIsStaticAlpha)
                {
                    // Rest a second time for Alphas
                    await SetStick(LEFT, 0, 10_000, 0_500, token).ConfigureAwait(false); // reset face forward
                    for (int i = 0; i < 2; i++)
                        await Click(A, 1_000, token).ConfigureAwait(false);
                    if (adv % 2 != 0)
                        await Click(DDOWN, 0_500, token).ConfigureAwait(false);
                    if (adv % 2 == 0)
                    {
                        for (int i = 0; i < 2; i++)
                            await Click(DUP, 0_500, token).ConfigureAwait(false);
                    }
                    Log("Resting...");
                    await Click(A, 12_000, token).ConfigureAwait(false);
                    Log("Rested twice!");
                    await Click(A, 1_000, token).ConfigureAwait(false);
                    // Rest a third time for Alphas
                    await SetStick(LEFT, 0, 10_000, 0_500, token).ConfigureAwait(false); // reset face forward
                    await ResetStick(token).ConfigureAwait(false);
                    for (int i = 0; i < 2; i++)
                        await Click(A, 1_000, token).ConfigureAwait(false);
                    if (adv % 2 == 0)
                        await Click(DDOWN, 0_500, token).ConfigureAwait(false);
                    if (adv % 2 != 0)
                    {
                        await Click(DUP, 0_500, token).ConfigureAwait(false);
                        await Click(DUP, 0_500, token).ConfigureAwait(false);
                    }
                    Log("Resting...");
                    await Click(A, 12_000, token).ConfigureAwait(false);
                    Log("Rested thrice!");
                    await Click(A, 1_000, token).ConfigureAwait(false);
                    //Night spawn only
                    if (Settings.AlphaScanConditions.NightSpawn)
                    {
                        await SetStick(LEFT, 0, 10_000, 0_500, token).ConfigureAwait(false); // reset face forward
                        await ResetStick(token).ConfigureAwait(false);
                        for (int i = 0; i < 2; i++)
                            await Click(A, 1_000, token).ConfigureAwait(false);
                        if (adv % 2 != 0)
                            await Click(DDOWN, 0_500, token).ConfigureAwait(false);
                        if (adv % 2 == 0)
                        {
                            await Click(DUP, 0_500, token).ConfigureAwait(false);
                            await Click(DUP, 0_500, token).ConfigureAwait(false);
                        }
                        Log("Resting...");
                        await Click(A, 12_000, token).ConfigureAwait(false);
                        Log("Rested for the night spawn!");
                        await Click(A, 1_000, token).ConfigureAwait(false);
                    }

                }
                await Click(DUP, 1_000, token).ConfigureAwait(false);
                await SetStick(LEFT, 0, 10_000, 0_500, token).ConfigureAwait(false); // reset face forward
                await ResetStick(token).ConfigureAwait(false);
                await TeleportToSpawnZone(token).ConfigureAwait(false);
                for (int i = 0; i < 2; i++)
                    await Click(B, 1_000, token).ConfigureAwait(false);

                if (Settings.AlphaScanConditions.IsSpawnInWater)
                    await Click(PLUS, 1_000, token).ConfigureAwait(false);

                Log("Trying to enter battle!");
                await PressAndHold(ZL, 1_000, 0, token).ConfigureAwait(false);
                await Click(ZR, 2_000, token).ConfigureAwait(false);
                await ResetStick(token).ConfigureAwait(false);
                var overworldcheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(OverworldOffset, 2, token).ConfigureAwait(false), 0);
                if (overworldcheck == 1)
                {
                    Log("Not in battle, trying again!");
                    int tries = 0;
                    while (overworldcheck == 1)
                    {
                        if (tries == 3)
                        {
                            Log("Tried 3 times, is the encounter present? Going back to camp to try again.");
                            await TeleportToCampZone(token).ConfigureAwait(false);
                            break;
                        }
                        await PressAndHold(ZL, 0_800, 0, token).ConfigureAwait(false);
                        await Click(ZR, 1_000, token).ConfigureAwait(false);
                        await ResetStick(token).ConfigureAwait(false); // reset
                        overworldcheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(OverworldOffset, 2, token).ConfigureAwait(false), 0);
                        if (overworldcheck != 1)
                            break;
                        await SetStick(LEFT, 0, -5_000, 0_500, token).ConfigureAwait(false); // turn around check encounter
                        await ResetStick(token).ConfigureAwait(false); // reset
                        await Click(ZL, 1_000, token).ConfigureAwait(false);
                        overworldcheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(OverworldOffset, 2, token).ConfigureAwait(false), 0);
                        if (overworldcheck != 1)
                            break;
                        await PressAndHold(ZL, 0_800, 0, token).ConfigureAwait(false);
                        await Click(ZR, 1_000, token).ConfigureAwait(false);
                        await ResetStick(token).ConfigureAwait(false); // reset
                        overworldcheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(OverworldOffset, 2, token).ConfigureAwait(false), 0);
                        if (overworldcheck != 1)
                            break;
                        await SetStick(LEFT, 0, 5_000, 0_500, token).ConfigureAwait(false); // turn around check encounter
                        await ResetStick(token).ConfigureAwait(false); // reset
                        await Click(ZL, 1_000, token).ConfigureAwait(false);
                        tries++;
                    }
                }
                if (overworldcheck == 0)
                {
                    ulong ofs = await NewParsePointer(WildPokemonPtrLA, token).ConfigureAwait(false);
                    var pk = await ReadPokemon(ofs, 0x168, token).ConfigureAwait(false);
                    if (pk != null)
                    {
                        var print = Hub.Config.StopConditions.GetAlphaPrintName(pk);
                        if (pk.IsAlpha)
                            alpha = "Alpha - ";
                        if (pk.IsShiny)
                        {
                            Log($"In battle with {print}!");
                            string url = TradeExtensions<PK9>.PokeImg(pk, false, false);
                            EchoUtil.EchoEmbed(Hub.Config.StopConditions.MatchFoundEchoMention, print, url, "", true);
                            Settings.AddCompletedShinyAlphaFound();

                            if (DumpSetting.Dump && !string.IsNullOrEmpty(DumpSetting.DumpFolder))
                                DumpPokemon(DumpSetting.DumpFolder, "advances", pk);

                            return;
                        }
                        Log($"Mashing A to knockout {alpha}{(Species)pk.Species}!");
                        while (overworldcheck != 1)
                        {
                            overworldcheck = BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(OverworldOffset, 2, token).ConfigureAwait(false), 0);
                            await Click(A, 1_000, token).ConfigureAwait(false);
                        }
                        Log($"Defeated {alpha}{(Species)pk.Species}! Returning to spawn point.");
                        alpha = string.Empty;
                    }
                }
                Log($"Returning to camp...");
                if (Settings.AlphaScanConditions.IsSpawnInWater)
                    await Click(PLUS, 1_000, token).ConfigureAwait(false);

                await TeleportToCampZone(token);
                await SetStick(LEFT, 0, 5_000, 0_500, token).ConfigureAwait(false); // reset face forward
                await ResetStick(token).ConfigureAwait(false); // reset
                await ArceusSaveGame(token).ConfigureAwait(false);
            }
        }
        Log("Advances reached! Shiny should be on the next respawn! Refer to the live map if unsure of frame!");
        await Click(HOME, 1_000, token).ConfigureAwait(false);
    }

    private void GetDefaultCoords()
    {
        var mode = Settings.SpecialConditions.ScanLocation;
        switch (mode)
        {
            case ArceusMap.ObsidianFieldlands: coordinates = ("43AC18AC", "4242DEC9", "4305C37C"); break;
            case ArceusMap.CrimsonMirelands: coordinates = ("436208ED", "425FD612", "43DBCD87"); break;
            case ArceusMap.CoronetHighlands: coordinates = ("44636F1B", "420FDC78", "446A1DE5"); break;
            case ArceusMap.CobaltCoastlands: coordinates = ("425E5D04", "4237E63D", "44207689"); break;
            case ArceusMap.AlabasterIcelands: coordinates = ("440B8982", "41F37461", "4467EAF7"); break;
        }

        Settings.SpecialConditions.SpawnZoneX = "4408C60D";
        Settings.SpecialConditions.SpawnZoneY = "4270D7B3";
        Settings.SpecialConditions.SpawnZoneZ = "43E52E0D";

        Settings.SpecialConditions.CampZoneX = coordinates.Item1;
        Settings.SpecialConditions.CampZoneY = coordinates.Item2;
        Settings.SpecialConditions.CampZoneZ = coordinates.Item3;
    }

    private void GetDefaultCampCoords()
    {
        var mode = Settings.SpecialConditions.ScanLocation;
        switch (mode)
        {
            case ArceusMap.ObsidianFieldlands: coordinates = ("43B7F23A", "424FF99B", "4308306A"); break;
            case ArceusMap.CrimsonMirelands: coordinates = ("43751CD5", "425E7B13", "43D92BA9"); break;
            case ArceusMap.CoronetHighlands: coordinates = ("445E8412", "4211C885", "4466973A"); break;
            case ArceusMap.CobaltCoastlands: coordinates = ("4291B987", "4234AEB4", "441BF96B"); break;
            case ArceusMap.AlabasterIcelands: coordinates = ("4404BA24", "41F91B54", "446417D3"); break;
        }

        Settings.SpecialConditions.CampZoneX = coordinates.Item1;
        Settings.SpecialConditions.CampZoneY = coordinates.Item2;
        Settings.SpecialConditions.CampZoneZ = coordinates.Item3;
    }

    private async Task ScanForSpawners(CancellationToken token)
    {
        Settings.AlphaScanConditions.StopOnMatch = false;
        int attempts = 1;
        for (int i = 0; i < 2; i++)
            await Click(B, 0_500, token).ConfigureAwait(false);
        Log("Searching for a match!");
        GetDefaultCoords();
        if (Settings.SpecialConditions.RunToProfessor == false && Settings.SpecialConditions.TypeOfLegend is not LegendsOnMap.Heatran or LegendsOnMap.Azelf or LegendsOnMap.Mesprit or LegendsOnMap.Uxie or LegendsOnMap.Manaphy)
            await TeleportToCampZone(token);
        while (!token.IsCancellationRequested)
        {
            Log($"Search #{attempts}");
            switch (Settings.BotType)
            {
                case
                    ArceusMode.StaticAlphaScan:
                    await SpawnerScan(token).ConfigureAwait(false); break;
                case
                    ArceusMode.GenieScanner:
                    await GenieScan(token).ConfigureAwait(false); break;
                case
                    ArceusMode.OtherLegendReset:
                    if (Settings.SpecialConditions.TypeOfLegend is LegendsOnMap.Heatran || Settings.SpecialConditions.TypeOfLegend is LegendsOnMap.Azelf ||
                        Settings.SpecialConditions.TypeOfLegend is LegendsOnMap.Mesprit || Settings.SpecialConditions.TypeOfLegend is LegendsOnMap.Uxie || Settings.SpecialConditions.TypeOfLegend is LegendsOnMap.Manaphy)
                    {
                        await Click(A, 0_800, token).ConfigureAwait(false);

                        while (!await IsOnOverworldTitle(token).ConfigureAwait(false))
                            await Click(A, 0_700, token).ConfigureAwait(false);

                    }
                    await OtherLegendsReset(token).ConfigureAwait(false); break;
            }

            if (Settings.AlphaScanConditions.StopOnMatch)
            {
                Log($"{Hub.Config.StopConditions.MatchFoundEchoMention} a match has been found!");
                await Click(HOME, 0_700, token).ConfigureAwait(false);
                if (Settings.ContinueAfterMatch == ContinueAfterMatch.PauseWaitAcknowledge)
                {
                    if (Settings.SpecialConditions.UsePlayerInvincibleCheat)
                    {
                        await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0xD65F03C0), MainNsoBase + InvincibleTrainer1, token).ConfigureAwait(false);
                        await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0xD65F03C0), MainNsoBase + InvincibleTrainer2, token).ConfigureAwait(false);
                    }

                    IsWaiting = true;
                    while (IsWaiting)
                        await Task.Delay(1_000, token).ConfigureAwait(false);

                    Settings.AlphaScanConditions.StopOnMatch = false;
                    await Click(HOME, 1_000, token).ConfigureAwait(false);
                }
                else
                    return;
            }
            if (Settings.SpecialConditions.TypeOfLegend is LegendsOnMap.Heatran || Settings.SpecialConditions.TypeOfLegend is LegendsOnMap.Azelf || Settings.SpecialConditions.TypeOfLegend is LegendsOnMap.Mesprit || Settings.SpecialConditions.TypeOfLegend is LegendsOnMap.Uxie)
            {
                await CloseGame(Hub.Config, token).ConfigureAwait(false);
                await StartGame(Hub.Config, token).ConfigureAwait(false);
                continue;
            }
            if (Settings.SpecialConditions.RunToProfessor == false)
            {
                await TeleportToCampZone(token);
                await SetStick(LEFT, -30_000, 0, 1_000, token).ConfigureAwait(false); // reset face forward
                await ResetStick(token).ConfigureAwait(false); // reset
                await Click(A, 1_000, token).ConfigureAwait(false);
                await Click(A, 1_000, token).ConfigureAwait(false);

                while (!await IsOnOverworldTitle(token).ConfigureAwait(false))
                    await Click(A, 1_000, token).ConfigureAwait(false);

                await TeleportToSpawnZone(token);
                await SetStick(LEFT, 0, -30_000, 1_000, token).ConfigureAwait(false); // reset face downward
                await ResetStick(token).ConfigureAwait(false); // reset
                for (int i = 0; i < 2; i++)
                    await Click(A, 1_000, token).ConfigureAwait(false);

                while (!await IsOnOverworldTitle(token).ConfigureAwait(false))
                    await Click(A, 1_000, token).ConfigureAwait(false);
            }
            if (Settings.SpecialConditions.RunToProfessor == true)
            {
                var mode = Settings.SpecialConditions.ScanLocation;
                switch (mode)
                {
                    case ArceusMap.ObsidianFieldlands: await SetStick(LEFT, 30_000, 32767, 1_000, token).ConfigureAwait(false); break;
                    case ArceusMap.CrimsonMirelands:
                        await SetStick(RIGHT, 19_500, 5000, 1_000, token).ConfigureAwait(false);
                        await SetStick(RIGHT, 0, 0, 0_500, token).ConfigureAwait(false);
                        await Task.Delay(1_000, token).ConfigureAwait(false);
                        await SetStick(LEFT, 0, 4000, 0, token).ConfigureAwait(false);
                        await ResetStick(token).ConfigureAwait(false); // reset
                        await Click(Y, 1_500, token).ConfigureAwait(false); break;
                    case ArceusMap.CobaltCoastlands:
                        await SetStick(RIGHT, 20_000, 5000, 1_000, token).ConfigureAwait(false);
                        await SetStick(RIGHT, 0, 0, 0_500, token).ConfigureAwait(false);
                        await Task.Delay(1_000, token).ConfigureAwait(false);
                        await SetStick(LEFT, 0, 32767, 1_000, token).ConfigureAwait(false); break;
                    case ArceusMap.CoronetHighlands:
                        await SetStick(RIGHT, 19_000, 5000, 1_000, token).ConfigureAwait(false);
                        await SetStick(RIGHT, 0, 0, 0_500, token).ConfigureAwait(false);
                        await Task.Delay(1_000, token).ConfigureAwait(false);
                        await SetStick(LEFT, 0, 32767, 1_200, token).ConfigureAwait(false); break;
                    case ArceusMap.AlabasterIcelands:
                        await SetStick(RIGHT, 19_000, 5000, 1_000, token).ConfigureAwait(false);
                        await SetStick(RIGHT, 0, 0, 0_500, token).ConfigureAwait(false);
                        await Task.Delay(1_000, token).ConfigureAwait(false);
                        await SetStick(LEFT, 0, 32767, 0_800, token).ConfigureAwait(false); break;
                }
                await ResetStick(token).ConfigureAwait(false); // reset

                for (int i = 0; i < 3; i++)
                    await Click(A, 1_000, token).ConfigureAwait(false);
                await Click(DDOWN, 1_000, token).ConfigureAwait(false);

                while (!await IsOnOverworldTitle(token).ConfigureAwait(false))
                    await Click(A, 1_000, token).ConfigureAwait(false);

                await SetStick(LEFT, 0, -30_000, 1_000, token).ConfigureAwait(false); // reset face downward
                await ResetStick(token).ConfigureAwait(false); // reset

                await Click(Y, 1_800, token).ConfigureAwait(false);
                while (!await IsOnOverworldTitle(token).ConfigureAwait(false))
                    await Click(A, 1_000, token).ConfigureAwait(false);
            }
            attempts++;
        }
    }

    private async Task TeleportToCampZone(CancellationToken token)
    {
        var ofs = await NewParsePointer(PlayerCoordPtrLA, token).ConfigureAwait(false);
        uint coordX1 = uint.Parse(Settings.SpecialConditions.CampZoneX, NumberStyles.AllowHexSpecifier);
        byte[] X1 = BitConverter.GetBytes(coordX1);
        uint coordY1 = uint.Parse(Settings.SpecialConditions.CampZoneY, NumberStyles.AllowHexSpecifier);
        byte[] Y1 = BitConverter.GetBytes(coordY1);
        uint coordZ1 = uint.Parse(Settings.SpecialConditions.CampZoneZ, NumberStyles.AllowHexSpecifier);
        byte[] Z1 = BitConverter.GetBytes(coordZ1);

        X1 = X1.Concat(Y1).Concat(Z1).ToArray();
        await SwitchConnection.WriteBytesAbsoluteAsync(X1, ofs, token).ConfigureAwait(false);

        await Task.Delay(Settings.SpecialConditions.WaitMsBetweenTeleports, token).ConfigureAwait(false);
    }

    private async Task TeleportToSpawnZone(CancellationToken token)
    {
        var ofs = await NewParsePointer(PlayerCoordPtrLA, token).ConfigureAwait(false);
        uint coordX1 = uint.Parse(Settings.SpecialConditions.SpawnZoneX, NumberStyles.AllowHexSpecifier);
        byte[] X1 = BitConverter.GetBytes(coordX1);
        uint coordY1 = uint.Parse(Settings.SpecialConditions.SpawnZoneY, NumberStyles.AllowHexSpecifier);
        byte[] Y1 = BitConverter.GetBytes(coordY1);
        uint coordZ1 = uint.Parse(Settings.SpecialConditions.SpawnZoneZ, NumberStyles.AllowHexSpecifier);
        byte[] Z1 = BitConverter.GetBytes(coordZ1);

        X1 = X1.Concat(Y1).Concat(Z1).ToArray();
        await SwitchConnection.WriteBytesAbsoluteAsync(X1, ofs, token).ConfigureAwait(false);

        await Task.Delay(Settings.SpecialConditions.WaitMsBetweenTeleports, token).ConfigureAwait(false);
    }

    private async Task TeleportToMMOGroupZone(CancellationToken token)
    {
        var ofs = await NewParsePointer(PlayerCoordPtrLA, token).ConfigureAwait(false);
        uint coordX1 = uint.Parse(coordinates.Item1, NumberStyles.AllowHexSpecifier);
        byte[] X1 = BitConverter.GetBytes(coordX1);
        uint coordY1 = uint.Parse(coordinates.Item2, NumberStyles.AllowHexSpecifier);
        byte[] Y1 = BitConverter.GetBytes(coordY1);
        uint coordZ1 = uint.Parse(coordinates.Item3, NumberStyles.AllowHexSpecifier);
        byte[] Z1 = BitConverter.GetBytes(coordZ1);

        X1 = X1.Concat(Y1).Concat(Z1).ToArray();
        await SwitchConnection.WriteBytesAbsoluteAsync(X1, ofs, token).ConfigureAwait(false);

        await Task.Delay(Settings.SpecialConditions.WaitMsBetweenTeleports, token).ConfigureAwait(false);
    }

    private async Task TeleportToDistortionZone(CancellationToken token)
    {
        await TeleportToDistortionZone(token, coordinates).ConfigureAwait(false);
    }

    private async Task TeleportToDistortionZone(CancellationToken token, (string, string, string) coords)
    {
        var ofs = await NewParsePointer(PlayerCoordPtrLA, token).ConfigureAwait(false);
        uint coordX1 = uint.Parse(coords.Item1, NumberStyles.AllowHexSpecifier);
        byte[] X1 = BitConverter.GetBytes(coordX1);
        uint coordY1 = uint.Parse(coords.Item2, NumberStyles.AllowHexSpecifier);
        byte[] Y1 = BitConverter.GetBytes(coordY1);
        uint coordZ1 = uint.Parse(coords.Item3, NumberStyles.AllowHexSpecifier);
        byte[] Z1 = BitConverter.GetBytes(coordZ1);

        X1 = X1.Concat(Y1).Concat(Z1).ToArray();
        await SwitchConnection.WriteBytesAbsoluteAsync(X1, ofs, token).ConfigureAwait(false);

        await Task.Delay(Settings.SpecialConditions.WaitMsBetweenTeleports, token).ConfigureAwait(false);
    }

    private async Task CaptureAnomalyDump(int currentIndex, int previousIndex, ulong matchedGroupSeed, ulong currentSpawnerAddress, int count, CancellationToken token)
    {
        try
        {
            var location = Settings.DistortionConditions.DistortionLocation;
            long locationArrayOffset = location switch
            {
                ArceusMap.ObsidianFieldlands => 0x990,
                ArceusMap.CrimsonMirelands => 0xC78,
                ArceusMap.CobaltCoastlands => 0xCC0,
                ArceusMap.CoronetHighlands => 0x818,
                ArceusMap.AlabasterIcelands => 0x948,
                _ => throw new NotImplementedException("Invalid distortion location."),
            };

            var containerBase = await SwitchConnection.PointerAll([0x42CC4D8, 0xC0, 0x1C0], token).ConfigureAwait(false);
            var arrayBase = containerBase + (ulong)locationArrayOffset;

            int paddedSlots = count + 4;
            var pointerArrayBytes = await SwitchConnection.ReadBytesAbsoluteAsync(arrayBase, paddedSlots * 8, token).ConfigureAwait(false);

            const int wideRadius = 0x400;
            var wideStart = currentSpawnerAddress - wideRadius;
            var wideBytes = await SwitchConnection.ReadBytesAbsoluteAsync(wideStart, wideRadius * 2, token).ConfigureAwait(false);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Anomaly dump -- {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Location: {location}");
            sb.AppendLine($"Matched consecutive groups: {previousIndex} and {currentIndex}");
            sb.AppendLine($"Matched group_seed: {matchedGroupSeed:X16}");
            sb.AppendLine($"Array container base (main+0x42CC4D8->+0xC0->+0x1C0): 0x{containerBase:X}");
            sb.AppendLine($"Per-location array start (+0x{locationArrayOffset:X}): 0x{arrayBase:X}");
            sb.AppendLine($"Current slot's resolved spawner address: 0x{currentSpawnerAddress:X}");
            sb.AppendLine();
            sb.AppendLine($"--- Raw pointer array, {paddedSlots} slots (coded count = {count}) ---");
            for (int s = 0; s < paddedSlots; s++)
            {
                var slotPtr = BitConverter.ToUInt64(pointerArrayBytes, s * 8);
                var flag = (s == currentIndex || s == previousIndex) ? "  <-- flagged" : (s >= count ? "  (padding, beyond coded count)" : "");
                sb.AppendLine($"  slot {s,3}: 0x{slotPtr:X16}{flag}");
            }
            sb.AppendLine();
            sb.AppendLine($"--- Wide raw dump around current slot's spawner address (0x{wideStart:X} to 0x{wideStart + (ulong)(wideRadius * 2):X}) ---");
            for (int off = 0; off < wideBytes.Length; off += 16)
            {
                var lineBytes = wideBytes.Skip(off).Take(16).ToArray();
                var hex = string.Join(" ", lineBytes.Select(b => b.ToString("X2")));
                sb.AppendLine($"  0x{wideStart + (ulong)off:X}: {hex}");
            }

            var dumpDir = ConsoleReportFolder("AnomalyDumps");
            var fileName = System.IO.Path.Combine(dumpDir, $"dump_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            await System.IO.File.WriteAllTextAsync(fileName, sb.ToString(), token).ConfigureAwait(false);

            Log($"[Anomaly] Groups {previousIndex} and {currentIndex} matched -- raw memory dumped to {fileName}");
            EchoUtil.Echo($"Anomaly detected: groups {previousIndex} and {currentIndex} in {location} matched identically. Dump saved to AnomalyDumps folder -- please send it over if you can.");
        }
        catch (Exception ex)
        {
            Log($"[Anomaly] Dump attempt failed: {ex.Message}");
        }
    }

    private async Task GenieScan(CancellationToken token)
    {
        await Task.Delay(0_500, token).ConfigureAwait(false);
        int groupID = Settings.SpecialConditions.ScanLocation switch
        {
            ArceusMap.ObsidianFieldlands => 310, //305 lando
            ArceusMap.CrimsonMirelands => 402, //398 enamo
            ArceusMap.CobaltCoastlands => 412, //408 thundo
            ArceusMap.AlabasterIcelands => 302, //297 torn
            _ => throw new NotImplementedException("Invalid scan location."),
        };
        string spawnerID = Settings.SpecialConditions.ScanLocation switch
        {
            ArceusMap.ObsidianFieldlands => "88AF9BCFDD5FCD8F",
            ArceusMap.CrimsonMirelands => "A468ADF5964CCE65",
            ArceusMap.AlabasterIcelands => "B8B7D33AB95F7A11",
            ArceusMap.CobaltCoastlands => "08398514506FBE25",
            _ => throw new NotImplementedException("Invalid scan location."),
        };

        bool isSpawner = false;
        while (!isSpawner)
        {
            var test = new long[] { 0x42a6ee0, 0x330, 0x70 + groupID * 0x440 + 0x20 + 0x3F0 };
            var stest = await SwitchConnection.PointerAll(test, token).ConfigureAwait(false);
            var bytes = BitConverter.ToUInt64(await SwitchConnection.ReadBytesAbsoluteAsync(stest, 8, token).ConfigureAwait(false), 0);
            isSpawner = $"{bytes:X16}".Equals(spawnerID);
            if (!isSpawner)
            {
                groupID--;
               // Log($"Bad SpawnerID {bytes:X16}\nTrying again with SpawnerID: {groupID}.");
                continue;
            }
           // Log($"Checking SpawnerID: {groupID}...");
            var SpawnerOffpoint = new long[] { 0x42a6ee0, 0x330, 0x70 + groupID * 0x440 + 0x20 };
            var SpawnerOff = await SwitchConnection.PointerAll(SpawnerOffpoint, token).ConfigureAwait(false);
            var GeneratorSeed = await SwitchConnection.ReadBytesAbsoluteAsync(SpawnerOff, 8, token).ConfigureAwait(false);

         //   Log($"SpawnerID {bytes:X16} - Generator Seed: {BitConverter.ToString(GeneratorSeed).Replace("-", "")}");
            var group_seed = (BitConverter.ToUInt64(GeneratorSeed, 0) - 0x82A2B175229D6A5B) & 0xFFFFFFFFFFFFFFFF;
            GenerateNextShiny(0, group_seed);
        }
    }

    private async Task OtherLegendsReset(CancellationToken token)
    {
        await Task.Delay(0_500, token).ConfigureAwait(false);
        int groupID = Settings.SpecialConditions.ScanLocation switch
        {
            ArceusMap.ObsidianFieldlands => 310,
            ArceusMap.CrimsonMirelands => 402,
            ArceusMap.CobaltCoastlands => 412,
            ArceusMap.AlabasterIcelands => 302,
            ArceusMap.CoronetHighlands => 265,
            _ => throw new NotImplementedException("Invalid scan location."),
        };
        string spawnerID = Settings.SpecialConditions.TypeOfLegend switch
        {
            LegendsOnMap.Shaymin => "CDE0EAB0B0192256",
            LegendsOnMap.Darkrai => "14044B0D5D36E4B6",
            LegendsOnMap.Cresselia => "80E30B44446F80BE",
            LegendsOnMap.Heatran => "DA1BB574FA53D58C",
            LegendsOnMap.Mesprit => "375CE0719E93B5CF",
            LegendsOnMap.Uxie => "5A67A8230FAF4B95",
            LegendsOnMap.Azelf => "33DB175E98E892F7",
            LegendsOnMap.Manaphy => "E1CBACBBA2D63665",
            _ => throw new NotImplementedException("Invalid scan location."),
        };

        bool isSpawner = false;
        while (!isSpawner)
        {
            var test = new long[] { 0x42a6ee0, 0x330, 0x70 + groupID * 0x440 + 0x20 + 0x3F0 };
            var stest = await SwitchConnection.PointerAll(test, token).ConfigureAwait(false);
            var bytes = BitConverter.ToUInt64(await SwitchConnection.ReadBytesAbsoluteAsync(stest, 8, token).ConfigureAwait(false), 0);
            isSpawner = $"{bytes:X16}".Equals(spawnerID);
            if (!isSpawner)
            {
                groupID--;
               // Log($"Bad SpawnerID {bytes:X16}\nTrying again with SpawnerID: {groupID}.");
                continue;
            }
          //  Log($"Checking SpawnerID: {groupID}...");
            var SpawnerOffpoint = new long[] { 0x42a6ee0, 0x330, 0x70 + groupID * 0x440 + 0x20 };
            var SpawnerOff = await SwitchConnection.PointerAll(SpawnerOffpoint, token).ConfigureAwait(false);
            var GeneratorSeed = await SwitchConnection.ReadBytesAbsoluteAsync(SpawnerOff, 8, token).ConfigureAwait(false);

           // Log($"SpawnerID {bytes:X16} - Generator Seed: {BitConverter.ToString(GeneratorSeed).Replace("-", "")}");
            var group_seed = (BitConverter.ToUInt64(GeneratorSeed, 0) - 0x82A2B175229D6A5B) & 0xFFFFFFFFFFFFFFFF;
            GenerateNextShiny(0, group_seed);
        }
    }

    private async Task ManaphyReset(CancellationToken token)
    {
        int count = 1;
        Log($"Starting Manaphy Scanner...");
        while (!token.IsCancellationRequested)
        {
            Log($"Search #{count}");
            await Click(A, 0_800, token).ConfigureAwait(false);

            while (!await IsOnOverworldTitle(token).ConfigureAwait(false))
                await Click(A, 0_800, token).ConfigureAwait(false);

            await Task.Delay(2_000, token).ConfigureAwait(false);

            for (int i = 409; i < 413; i++)
            {
                string spawnerID = i switch
                {
                    409 => "97D85B3BB18FD0AB",
                    410 => "7C9E6D810B343908",
                    411 => "162F766DB48FC6D8",
                    412 => "E1CBACBBA2D63665",
                    _ => throw new NotImplementedException("Invalid spawner."),
                };
                var test = new long[] { 0x42a6ee0, 0x330, 0x70 + i * 0x440 + 0x20 + 0x3F0 };
                var stest = await SwitchConnection.PointerAll(test, token).ConfigureAwait(false);
                var bytes = BitConverter.ToUInt64(await SwitchConnection.ReadBytesAbsoluteAsync(stest, 8, token).ConfigureAwait(false), 0);
                bool isSpawner = $"{bytes:X16}".Equals(spawnerID);
                if (!isSpawner)
                {
                    i--;
                  //  Log($"Bad SpawnerID {bytes:X16}\nTrying again with SpawnerID: {i}.");
                    continue;
                }
                //  Log($"Checking SpawnerID: {i}...");
                var SpawnerOffpoint = new long[] { 0x42a6ee0, 0x330, 0x70 + i * 0x440 + 0x20 };
                var SpawnerOff = await SwitchConnection.PointerAll(SpawnerOffpoint, token).ConfigureAwait(false);
                var GeneratorSeed = await SwitchConnection.ReadBytesAbsoluteAsync(SpawnerOff, 8, token).ConfigureAwait(false);

                //  Log($"SpawnerID {bytes:X16} - Generator Seed: {BitConverter.ToString(GeneratorSeed).Replace("-", "")}");
                var group_seed = (BitConverter.ToUInt64(GeneratorSeed, 0) - 0x82A2B175229D6A5B) & 0xFFFFFFFFFFFFFFFF;
                if (i == 409)
                    GenerateCaveFamily(true, group_seed);
                else
                    GenerateCaveFamily(false, group_seed);

                if (Settings.AlphaScanConditions.StopOnMatch)
                {
                    Log($"{Hub.Config.StopConditions.MatchFoundEchoMention} a match has been found!");
                    await Click(HOME, 0_700, token).ConfigureAwait(false);
                    if (Settings.ContinueAfterMatch == ContinueAfterMatch.PauseWaitAcknowledge)
                    {
                        if (Settings.SpecialConditions.UsePlayerInvincibleCheat)
                        {
                            await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0xD65F03C0), MainNsoBase + InvincibleTrainer1, token).ConfigureAwait(false);
                            await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0xD65F03C0), MainNsoBase + InvincibleTrainer2, token).ConfigureAwait(false);
                        }

                        IsWaiting = true;
                        while (IsWaiting)
                            await Task.Delay(1_000, token).ConfigureAwait(false);

                        Settings.AlphaScanConditions.StopOnMatch = false;
                        await Click(HOME, 1_000, token).ConfigureAwait(false);
                    }
                    else
                        return;
                }
            }
            count++;

            await CloseGame(Hub.Config, token).ConfigureAwait(false);
            await StartGame(Hub.Config, token).ConfigureAwait(false);
        }
    }

    private async Task SpawnerScan(CancellationToken token)
    {
        await Task.Delay(0_500, token).ConfigureAwait(false);
        Log($"Starting Alpha Scanner for {Settings.SpecialConditions.ScanLocation}...");
        int alphacount = Settings.SpecialConditions.ScanLocation switch
        {
            ArceusMap.ObsidianFieldlands or ArceusMap.CobaltCoastlands => 18,
            ArceusMap.CrimsonMirelands => 19,
            ArceusMap.CoronetHighlands => 15,
            ArceusMap.AlabasterIcelands => 13,
            _ => throw new NotImplementedException("Invalid scan location."),
        };

        for (int i = 0; i < alphacount; i++)
        {
            var SpawnerOffpoint = new long[] { 0x42a6ee0, 0x330, 0x70 + i * 0x440 + 0x20 };
            var SpawnerOff = await SwitchConnection.PointerAll(SpawnerOffpoint, token).ConfigureAwait(false);
            var GeneratorSeed = await SwitchConnection.ReadBytesAbsoluteAsync(SpawnerOff, 8, token).ConfigureAwait(false);
            var group_seed = (BitConverter.ToUInt64(GeneratorSeed, 0) - 0x82A2B175229D6A5B) & 0xFFFFFFFFFFFFFFFF;
            Log($"Generator Seed: {BitConverter.ToString(GeneratorSeed).Replace("-", "")}\nGroup Seed: {string.Format("0x{0:X}", group_seed)}");
            GenerateNextShiny(i, group_seed);
        }
    }

    private (List<PA8>, List<PA8>) ReadMMOSeed(PokedexSaveData dex, int totalspawn, ulong group_seed, int bonuscount, ulong encslot, ulong bonusencslot)
    {
        List<PA8> monlist = new();
        List<PA8> bonuslist = new();
        var groupseed = group_seed;
        var mainrng = new Xoroshiro128Plus(groupseed);
        var sum = GrabEncounterSum(encslot, bonusencslot);

        for (int i = 0; i < 4; i++)
        {
            var spawner_seed = mainrng.Next();
            var spawner_rng = new Xoroshiro128Plus(spawner_seed);
            var encounter_slot = spawner_rng.Next() / Math.Pow(2, 64) * sum.Item1;
            var fixedseed = spawner_rng.Next();
            mainrng.Next();
            var poke = GrabMMOSpecies(encounter_slot, encslot);
            var gt = PersonalTable.LA.GetFormEntry(poke.Item1.Species, poke.Item1.Form).Gender;

            var (perfect, complete) = CheckForPerfectComplete(HasCharm, dex, poke.Item1.Species);
            var rolls = 1 + (complete ? 1 : 0) + (perfect ? 2 : 0) + (HasCharm ? 3 : 0) + (int)(SpawnType.MMO - 7);

            var gen = GenerateFromSeed(fixedseed, rolls, poke.Item2, gt);
            poke.Item1.EncryptionConstant = gen.EC;
            poke.Item1.PID = gen.PID;
            int[] pkIVList = gen.IVs;
            poke.Item1.SetIVs(pkIVList);
            (pkIVList[5], pkIVList[3], pkIVList[4]) = (pkIVList[3], pkIVList[4], pkIVList[5]);
            poke.Item1.IVs = pkIVList;
            poke.Item1.Nature = (Nature)gen.Item8;
            poke.Item1.Gender = (byte)gen.gender;

            if (gen.shiny)
                CommonEdits.SetShiny(poke.Item1, Shiny.Always);

            monlist.Add(poke.Item1);
        }

        groupseed = mainrng.Next();
        mainrng = new Xoroshiro128Plus(groupseed);
        var respawnrng = new Xoroshiro128Plus(groupseed);
        for (int r = 0; r < totalspawn - 4; r++)
        {
            var spawner_seed = respawnrng.Next();
            respawnrng.Next();
            respawnrng = new Xoroshiro128Plus(respawnrng.Next());
            var fixed_rng = new Xoroshiro128Plus(spawner_seed);
            var encounter_slot = fixed_rng.Next() / Math.Pow(2, 64) * sum.Item1;
            var fixed_seed = fixed_rng.Next();

            var poke = GrabMMOSpecies(encounter_slot, encslot);
            var gt = PersonalTable.LA.GetFormEntry(poke.Item1.Species, poke.Item1.Form).Gender;

            var (perfect, complete) = CheckForPerfectComplete(HasCharm, dex, poke.Item1.Species);
            var rolls = 1 + (complete ? 1 : 0) + (perfect ? 2 : 0) + (HasCharm ? 3 : 0) + (int)(SpawnType.MMO - 7);

            var gen = GenerateFromSeed(fixed_seed, rolls, poke.Item2, gt);
            int[] pkIVList = gen.IVs;
            poke.Item1.SetIVs(pkIVList);
            (pkIVList[5], pkIVList[3], pkIVList[4]) = (pkIVList[3], pkIVList[4], pkIVList[5]);
            poke.Item1.IVs = pkIVList;
            poke.Item1.EncryptionConstant = gen.EC;
            poke.Item1.PID = gen.PID;
            poke.Item1.Nature = (Nature)(int)gen.Item8;
            poke.Item1.Gender = (byte)gen.gender;

            if (gen.shiny)
                CommonEdits.SetShiny(poke.Item1, Shiny.Always);

            monlist.Add(poke.Item1);
        }

        //Bonus round
        if (sum.Item2 != 0)
        {
            var bonus_seed = respawnrng.Next() - 0x82A2B175229D6A5B & 0xFFFFFFFFFFFFFFFF;
            mainrng = new Xoroshiro128Plus(bonus_seed);
            for (int i = 0; i < 4; i++)
            {
                var spawner_seed = mainrng.Next();
                var spawner_rng = new Xoroshiro128Plus(spawner_seed);
                var encounter_slot = spawner_rng.Next() / Math.Pow(2, 64) * sum.Item2;
                var fixedseed = spawner_rng.Next();
                mainrng.Next();

                var poke = GrabMMOSpecies(encounter_slot, bonusencslot);
                var gt = PersonalTable.LA.GetFormEntry(poke.Item1.Species, poke.Item1.Form).Gender;

                var (perfect, complete) = CheckForPerfectComplete(HasCharm, dex, poke.Item1.Species);
                var rolls = 1 + (complete ? 1 : 0) + (perfect ? 2 : 0) + (HasCharm ? 3 : 0) + (int)(SpawnType.MMO - 7);

                var gen = GenerateFromSeed(fixedseed, rolls, poke.Item2, gt);
                int[] pkIVList = gen.IVs;
                poke.Item1.SetIVs(pkIVList);
                (pkIVList[5], pkIVList[3], pkIVList[4]) = (pkIVList[3], pkIVList[4], pkIVList[5]);
                poke.Item1.IVs = pkIVList;
                poke.Item1.EncryptionConstant = gen.EC;
                poke.Item1.PID = gen.PID;
                poke.Item1.Nature = (Nature)gen.Item8;
                poke.Item1.Gender = (byte)gen.gender;

                if (gen.shiny)
                    CommonEdits.SetShiny(poke.Item1, Shiny.Always);

                bonuslist.Add(poke.Item1);
            }

            bonus_seed = mainrng.Next();
            mainrng = new Xoroshiro128Plus(bonus_seed);
            var bonusrng = new Xoroshiro128Plus(bonus_seed);
            for (int r = 0; r < bonuscount - 4; r++)
            {
                var bonusspawner_seed = bonusrng.Next();
                bonusrng.Next();
                bonusrng = new Xoroshiro128Plus(bonusrng.Next());
                var fixed_rng = new Xoroshiro128Plus(bonusspawner_seed);
                var encounter_slot = fixed_rng.Next() / Math.Pow(2, 64) * sum.Item2;
                var fixed_seed = fixed_rng.Next();

                var poke = GrabMMOSpecies(encounter_slot, bonusencslot);
                var gt = PersonalTable.LA.GetFormEntry(poke.Item1.Species, poke.Item1.Form).Gender;

                var (perfect, complete) = CheckForPerfectComplete(HasCharm, dex, poke.Item1.Species);
                var rolls = 1 + (complete ? 1 : 0) + (perfect ? 2 : 0) + (HasCharm ? 3 : 0) + (int)(SpawnType.MMO - 7);

                var gen = GenerateFromSeed(fixed_seed, rolls, poke.Item2, gt);
                poke.Item1.EncryptionConstant = gen.EC;
                poke.Item1.PID = gen.PID;
                int[] pkIVList = gen.IVs;
                poke.Item1.SetIVs(pkIVList);
                (pkIVList[5], pkIVList[3], pkIVList[4]) = (pkIVList[3], pkIVList[4], pkIVList[5]);
                poke.Item1.IVs = pkIVList;
                poke.Item1.Nature = (Nature)gen.Item8;
                poke.Item1.Gender = (byte)gen.gender;

                if (gen.shiny)
                    CommonEdits.SetShiny(poke.Item1, Shiny.Always);

                bonuslist.Add(poke.Item1);
            }
        }
        return (monlist, bonuslist);
    }

    private List<PA8> ReadOutbreakSeed(PokedexSaveData dex, Species species, int totalspawn, ulong groupseed)
    {
        List<PA8> monlist = new();
        PA8 pk = new() { Species = (ushort)species };
        int givs = 0;
        var mainrng = new Xoroshiro128Plus(groupseed);
        for (int i = 0; i < 4; i++)
        {
            var spawner_seed = mainrng.Next();
            mainrng.Next();
            var spawner_rng = new Xoroshiro128Plus(spawner_seed);
            var slot = spawner_rng.Next() / Math.Pow(2, 64) * 101;
            var alpha = slot >= 100;
            var fixedseed = spawner_rng.Next();

            if (alpha)
                givs = 3;
            var gt = PersonalTable.LA.GetFormEntry(pk.Species, pk.Form).Gender;

            var (perfect, complete) = CheckForPerfectComplete(HasCharm, dex, pk.Species);
            var rolls = 1 + (complete ? 1 : 0) + (perfect ? 2 : 0) + (HasCharm ? 3 : 0) + (int)(SpawnType.Outbreak - 7);

            var gen = GenerateFromSeed(fixedseed, rolls, givs, gt);
            pk.Species = (ushort)species;
            pk.EncryptionConstant = gen.EC;
            pk.PID = gen.PID;
            int[] pkIVList = gen.IVs;
            pk.SetIVs(pkIVList);
            (pkIVList[5], pkIVList[3], pkIVList[4]) = (pkIVList[3], pkIVList[4], pkIVList[5]);
            pk.IVs = pkIVList;
            pk.Nature = (Nature)gen.Item8;
            pk.Gender = (byte)gen.gender;
            pk.IsAlpha = alpha;

            if (gen.shiny)
                CheckEmbed(pk, "", "", CancellationToken.None).ConfigureAwait(false);

            monlist.Add(pk);
            givs = 0;
            pk = new PA8();
        }

        groupseed = mainrng.Next();
        mainrng = new Xoroshiro128Plus(groupseed);
        var respawnrng = new Xoroshiro128Plus(groupseed);
        for (int r = 0; r < totalspawn - 4; r++)
        {
            var spawner_seed = respawnrng.Next();
            respawnrng.Next();
            respawnrng = new Xoroshiro128Plus(respawnrng.Next());
            var fixed_rng = new Xoroshiro128Plus(spawner_seed);
            var slot = fixed_rng.Next() / Math.Pow(2, 64) * 101;
            var alpha = slot >= 100;
            var fixed_seed = fixed_rng.Next();

            if (alpha)
                givs = 3;
            var gt = PersonalTable.LA.GetFormEntry(pk.Species, pk.Form).Gender;

            var (perfect, complete) = CheckForPerfectComplete(HasCharm, dex, pk.Species);
            var rolls = 1 + (complete ? 1 : 0) + (perfect ? 2 : 0) + (HasCharm ? 3 : 0) + (int)(SpawnType.Outbreak - 7);

            var gen = GenerateFromSeed(fixed_seed, rolls, givs, gt);
            pk.Species = (ushort)species;
            pk.EncryptionConstant = gen.EC;
            pk.PID = gen.PID;
            int[] pkIVList = gen.IVs;
            pk.SetIVs(pkIVList);
            (pkIVList[5], pkIVList[3], pkIVList[4]) = (pkIVList[3], pkIVList[4], pkIVList[5]);
            pk.IVs = pkIVList;
            pk.Nature = (Nature)gen.Item8;
            pk.IsAlpha = alpha;
            pk.Gender = (byte)gen.gender;

            if (gen.shiny)
                CheckEmbed(pk, "", "", CancellationToken.None).ConfigureAwait(false);

            monlist.Add(pk);
            givs = 0;
            pk = new PA8();
        }

        return monlist;
    }

    private (PA8 match, bool shiny, string log) ReadDistortionSeed(PokedexSaveData dex, int id, ulong group_seed, int encslotsum, int common_sum)
    {
        string logs = string.Empty;
        PA8 pk = new();
        var sum_to_use = 0;
        if (id == 0 || id == 4 || id == 8 || id == 12 || id == 16 || id == 20)
            sum_to_use = common_sum;
        else if (id != 0 && id != 4 && id != 8 && id != 12 && id != 16 && id != 20)
            sum_to_use = encslotsum;

        var groupseed = group_seed;
        var mainrng = new Xoroshiro128Plus(groupseed);
        var generator_seed = mainrng.Next();
        var rng = new Xoroshiro128Plus(generator_seed);
        var encounter_slot = rng.Next() / Math.Pow(2, 64) * sum_to_use;
        var fixedseed = rng.Next();
        var givs = 0;
        if (id == 0 || id == 4 || id == 8 || id == 12 || id == 16 || id == 20 || id == 24)
            pk = GetCommonDistortionSpecies(encounter_slot);
        else if (id != 0 && id != 4 && id != 8 && id != 12 && id != 16 && id != 20 && id != 24)
            pk = GetDistortionSpecies(encounter_slot);

        if (pk.IsAlpha)
        {
            givs = 3;
            if (Settings.DistortionConditions.AnyAlpha)
                FillDistortionCoords(id);
        }
        var gt = PersonalTable.LA.GetFormEntry(pk.Species, pk.Form).Gender;
        var (perfect, complete) = CheckForPerfectComplete(HasCharm, dex, pk.Species);
        var rolls = 1 + (complete ? 1 : 0) + (perfect ? 2 : 0) + (HasCharm ? 3 : 0) + (int)(SpawnType.Regular - 7);

        var previewRng = new Xoroshiro128Plus(fixedseed);
        previewRng.NextInt();
        var sidtid = (uint)previewRng.NextInt();

        var gen = GenerateFromSeed(fixedseed, rolls, givs, gt);

        string location = GetDistortionSpeciesLocation(id);
        if (id >= 9 && id <= 12 && Settings.DistortionConditions.DistortionLocation == ArceusMap.CrimsonMirelands)
        {
            logs += $"Ignoring Spawner from GroupID: {id} as location currently unknown.";
            return (pk, false, logs);
        }

        pk.EncryptionConstant = gen.EC;
        pk.PID = gen.PID;
        int[] pkIVList = gen.IVs;
        pk.SetIVs(pkIVList);
        (pkIVList[5], pkIVList[3], pkIVList[4]) = (pkIVList[3], pkIVList[4], pkIVList[5]);
        pk.IVs = pkIVList;
        pk.Nature = (Nature)gen.Item8;
        pk.Gender = (byte)gen.gender;

        pk.TID16 = (ushort)(sidtid & 0xFFFF);
        pk.SID16 = (ushort)(sidtid >> 16);

        if (gen.shiny)
        {
            FillDistortionCoords(id);
        }

        if (!pk.IsShiny && pk.IsAlpha)
            Log($"Alpha {(Species)pk.Species} GroupID: {id} found at Location: {location}");

        var print = Hub.Config.StopConditions.GetAlphaPrintName(pk);
        logs += $"\nGenerator Seed: {(group_seed + 0x82A2B175229D6A5B & 0xFFFFFFFFFFFFFFFF):X16}\nGroup: {id}{print}\nEncounter Slot: {encounter_slot}\nLocation: {location}\n";
        mainrng.Next();
        mainrng.Next();
        _ = new Xoroshiro128Plus(mainrng.Next());
        return (pk, gen.shiny, logs);
    }

    private void GenerateCaveFamily(bool mana, ulong seed)
    {
        PA8 pk = new();
        var mainrng = new Xoroshiro128Plus(seed);
        for (int i = 0; i < Settings.SpecialConditions.MaxAdvancesToSearch; i++)
        {
            var generator_seed = mainrng.Next();
            mainrng.Next();
            var rng = new Xoroshiro128Plus(generator_seed);
            rng.Next();
            var givs = mana ? 3 : 0;
            var gen = GenerateFromSeed(rng.Next(), 1, givs, 0);
            if (mana)
                pk.Species = (ushort)Species.Manaphy;
            if (!mana)
                pk.Species = (ushort)Species.Phione;
            pk.EncryptionConstant = gen.EC;
            pk.PID = gen.PID;
            pk.IV_HP = gen.IVs[0];
            pk.IV_ATK = gen.IVs[1];
            pk.IV_DEF = gen.IVs[2];
            pk.IV_SPA = gen.IVs[3];
            pk.IV_SPD = gen.IVs[4];
            pk.IV_SPE = gen.IVs[5];
            pk.Nature = (Nature)gen.nature;
            if (StopConditionSettings.EncounterFound(pk, DesiredMinIVs, DesiredMaxIVs, Hub.Config.StopConditions, null))
            {
                Settings.AlphaScanConditions.StopOnMatch = true;
                string url = TradeExtensions<PK9>.PokeImg(pk, false, false);
                var print = Hub.Config.StopConditions.GetAlphaPrintName(pk);
                EchoUtil.EchoEmbed(Hub.Config.StopConditions.MatchFoundEchoMention, print, url, "", true);
            }
          //  Log($"\n{(Species)pk.Species}\nEC: {pk.EncryptionConstant:X8}\nPID: {pk.PID:X8}\nIVs: {pk.IV_HP}/{pk.IV_ATK}/{pk.IV_DEF}/{pk.IV_SPA}/{pk.IV_SPD}/{pk.IV_SPE}\nNature: {(Nature)pk.Nature}\nGenerator Seed: {generator_seed:X16}");
        }
    }

    private (ulong, PA8) GenerateNextShiny(int spawnerid, ulong seed)
    {
        PA8 pk = new();
        int hits = 0;
        ulong newseed = 0;
        var mainrng = new Xoroshiro128Plus(seed);
        int givs = 0;
        if (Settings.AlphaScanConditions.SpawnIsStaticAlpha)
            givs = 3;

        if (!Settings.AlphaScanConditions.InItSpawn)
        {
            mainrng.Next();
            mainrng.Next();
            mainrng = new Xoroshiro128Plus(mainrng.Next());
        }

        if (Settings.BotType == ArceusMode.GenieScanner || Settings.BotType == ArceusMode.OtherLegendReset)
        {
            Species species = Species.None;
            for (int i = 0; i < Settings.SpecialConditions.MaxAdvancesToSearch; i++)
            {
                if (Settings.BotType == ArceusMode.GenieScanner)
                {
                    species = Settings.SpecialConditions.ScanLocation switch
                    {
                        ArceusMap.ObsidianFieldlands => Species.Landorus,
                        ArceusMap.CrimsonMirelands => Species.Enamorus,
                        ArceusMap.CobaltCoastlands => Species.Thundurus,
                        ArceusMap.AlabasterIcelands => Species.Tornadus,
                        _ => throw new NotImplementedException("Invalid location."),
                    };
                }

                if (Settings.BotType == ArceusMode.OtherLegendReset)
                {
                    species = Settings.SpecialConditions.TypeOfLegend switch
                    {
                        LegendsOnMap.Heatran => Species.Heatran,
                        LegendsOnMap.Azelf => Species.Azelf,
                        LegendsOnMap.Mesprit => Species.Mesprit,
                        LegendsOnMap.Uxie => Species.Uxie,
                        LegendsOnMap.Cresselia => Species.Cresselia,
                        LegendsOnMap.Shaymin => Species.Shaymin,
                        LegendsOnMap.Darkrai => Species.Darkrai,
                        LegendsOnMap.Manaphy => Species.Manaphy,
                        _ => throw new NotImplementedException("Invalid location."),
                    };
                }

                var generator_seed = mainrng.Next();
                mainrng.Next();
                var rng = new Xoroshiro128Plus(generator_seed);
                rng.Next();

                var genderratio = Settings.SpecialConditions.TypeOfLegend is LegendsOnMap.Heatran ? 127 : 0;
                var gen = GenerateFromSeed(rng.Next(), 1, 3, genderratio);
                pk.Species = (ushort)species;
                pk.EncryptionConstant = gen.EC;
                pk.PID = gen.PID;
                pk.IV_HP = gen.IVs[0];
                pk.IV_ATK = gen.IVs[1];
                pk.IV_DEF = gen.IVs[2];
                pk.IV_SPA = gen.IVs[3];
                pk.IV_SPD = gen.IVs[4];
                pk.IV_SPE = gen.IVs[5];
                pk.Nature = (Nature)gen.nature;
                pk.Gender = (byte)gen.gender;

                if (StopConditionSettings.EncounterFound(pk, DesiredMinIVs, DesiredMaxIVs, Hub.Config.StopConditions, null))
                {
                    Log($"\nAdvance: {i} - {species}\nEC: {pk.EncryptionConstant:X8}\nPID: {pk.PID:X8}\nIVs: {pk.IV_HP}/{pk.IV_ATK}/{pk.IV_DEF}/{pk.IV_SPA}/{pk.IV_SPD}/{pk.IV_SPE}\nNature: {pk.Nature}\nGenerator Seed: {generator_seed:X16}");
                    Settings.AlphaScanConditions.StopOnMatch = true;
                    string url = TradeExtensions<PK9>.PokeImg(pk, false, false);
                    var print = Hub.Config.StopConditions.GetAlphaPrintName(pk);
                    EchoUtil.EchoEmbed(Hub.Config.StopConditions.MatchFoundEchoMention, print, url, "", true);
                    return (newseed, pk);
                }
               // Log($"\nAdvance: {i} - {species}\nEC: {pk.EncryptionConstant:X8}\nPID: {pk.PID:X8}\nIVs: {pk.IV_HP}/{pk.IV_ATK}/{pk.IV_DEF}/{pk.IV_SPA}/{pk.IV_SPD}/{pk.IV_SPE}\nNature: {pk.Nature}\nGenerator Seed: {generator_seed:X16}");

                mainrng = new Xoroshiro128Plus(mainrng.Next());
            }

        }
        else if (Settings.BotType != ArceusMode.GenieScanner && Settings.BotType != ArceusMode.OtherLegendReset)
        {
            string species = Settings.SpecialConditions.ScanLocation switch
            {
                ArceusMap.ObsidianFieldlands => ObsidianTitle[spawnerid],
                ArceusMap.CrimsonMirelands => CrimsonTitle[spawnerid],
                ArceusMap.CoronetHighlands => CoronetTitle[spawnerid],
                ArceusMap.CobaltCoastlands => CobaltTitle[spawnerid],
                ArceusMap.AlabasterIcelands => AlabasterTitle[spawnerid],
                _ => throw new NotImplementedException("Invalid spawner ID or location."),
            };

            for (int i = 0; i < Settings.SpecialConditions.MaxAdvancesToSearch; i++)
            {
                var generator_seed = mainrng.Next();
                mainrng.Next();
                var rng = new Xoroshiro128Plus(generator_seed);
                rng.Next();

                var gen = GenerateFromSeed(rng.Next(), (int)Settings.AlphaScanConditions.StaticAlphaShinyRolls, givs, 0);
               // Log($"\nAdvances: {i}\nAlpha: {species} - {gen.shinyXor} | SpawnerID: {spawnerid}\nEC: {gen.EC:X8}\nPID: {gen.PID:X8}\nIVs: {string.Join("/", gen.IVs)}\nNature: {gen.Item8}\nSeed: {gen.Item9:X16}");
                if (gen.shiny)
                {
                    if (Settings.SpeciesToHunt.Length != 0 && !Settings.SpeciesToHunt.Contains(species))
                        break;

                    if (StopConditionSettings.EncounterFound(pk, DesiredMinIVs, DesiredMaxIVs, Hub.Config.StopConditions, null))
                    {
                        //Log($"\nAdvances: {i}\nAlpha: {species} - {gen.shinyXor} | SpawnerID: {spawnerid}\nEC: {gen.EC:X8}\nPID: {gen.PID:X8}\nIVs: {string.Join("/", gen.IVs)}\nNature: {gen.Item8}\nSeed: {gen.Item9:X16}");
                        newseed = generator_seed;
                        Settings.AlphaScanConditions.StopOnMatch = true;
                        hits++;

                        if (hits == 3)
                        {
                           // Log($"First three shiny results for {species} found.");
                            break;
                        }
                    }
                }
                mainrng = new Xoroshiro128Plus(mainrng.Next());
               // if (i == Settings.SpecialConditions.MaxAdvancesToSearch - 1 && !gen.shiny);
                    //Log($"No results within {Settings.SpecialConditions.MaxAdvancesToSearch} advances for {species} | SpawnerID: {spawnerid}.");
            }
        }

        return (newseed, pk);
    }

    private async Task PerformOutbreakScan(PokedexSaveData dex, CancellationToken token)
    {
        List<string> speclist = [];
        List<string> result = [];
        string[] list = Settings.SpeciesToHunt.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var ofs = new long[] { 0x42BA6B0, 0x2B0, 0x58, 0x18, 0x20 };
        var outbreakptr = await SwitchConnection.PointerAll(ofs, token).ConfigureAwait(false);
        var info = await SwitchConnection.ReadBytesAbsoluteAsync(outbreakptr, 0x190, token).ConfigureAwait(false);
        for (int i = 0; i < 4; i++)
        {
            int count = 0;
            Species outbreakspecies = (Species)BitConverter.ToUInt16(info.AsSpan(0 + (i * 80), 2));
            if (outbreakspecies != Species.None)
            {
                var outbreakseed = BitConverter.ToUInt64(info.AsSpan(56 + (i * 80), 8));
                var spawncount = BitConverter.ToUInt16(info.AsSpan(64 + (i * 80), 2));
                result.Add($"\nOutbreak found for: {outbreakspecies} | Total Spawn Count: {spawncount}.");
                var monlist = ReadOutbreakSeed(dex, outbreakspecies, spawncount, outbreakseed);
                foreach (PA8 pk in monlist)
                {
                    count++;
                    var print = Hub.Config.StopConditions.GetAlphaPrintName(pk);
                    var msg = $"\nOutbreak Spawn: #{count}" + print;
                    speclist.Add(msg);

                    if (pk.IsShiny && Settings.OutbreakConditions.AlphaShinyOnly && pk.IsAlpha || pk.IsShiny && !Settings.OutbreakConditions.AlphaShinyOnly)
                    {
                        await CheckEmbed(pk, "", msg, token).ConfigureAwait(false);
                    }
                }
            }
            if (list.Contains($"{outbreakspecies}") && Settings.OutbreakConditions.StopIfSpeciesFound)
            {
                Log($"{Hub.Config.StopConditions.MatchFoundEchoMention} Desired species {outbreakspecies} was found!");
                IsWaiting = true;
                while (IsWaiting)
                    await Task.Delay(1_000, token).ConfigureAwait(false);
            }
        }
        var rez = string.Join("", result);
        Log(rez);
    }

    private static (int, int) GrabEncounterSum(ulong encslot, ulong bonusslot)
    {
        int encmax = 0;
        int bonusmax = 0;
        foreach (var keyValuePair in SpawnGenerator.EncounterTables)
        {
            if (keyValuePair.Key == encslot)
            {
                foreach (var keyValue in keyValuePair.Value)
                    encmax += keyValue.Rate;
            }

            if (keyValuePair.Key == bonusslot)
            {
                foreach (var keyValue in keyValuePair.Value)
                    bonusmax += keyValue.Rate;
            }
        }
        return (encmax, bonusmax);
    }

    private static (PA8, int) GrabMMOSpecies(double encounter_slot, ulong encslot)
    {
        var encmax = 0;
        foreach (var keyValuePair in SpawnGenerator.EncounterTables)
        {
            if (keyValuePair.Key == encslot)
            {
                foreach (var keyValue in keyValuePair.Value)
                {
                    encmax += keyValue.Rate;
                    if (encounter_slot < encmax)
                        return (new PA8 { Species = keyValue.Species, Form = (byte)keyValue.Form, IsAlpha = keyValue.IsAlpha }, keyValue.FlawlessIVs);
                }
            }
        }
        return (new(), 0);
    }

    private async Task CheckEmbed(PA8 pk, string map, string spawn, CancellationToken token)
    {
        string url = string.Empty;
        var print = $"{Hub.Config.StopConditions.GetSpecialPrintName(pk)}";
        string[] list = Settings.SpeciesToHunt.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        bool huntedspecies = list.Contains($"{(Species)pk.Species}");
        if (string.IsNullOrEmpty(map) && Hub.Config.ArceusLA.OutbreakConditions.TypeOfScan == OutbreakScanType.OutbreakOnly)
        {
            Log($"Outbreak for {(Species)pk.Species} has been found! Stopping routine execution!");
            IsWaiting = true;
            while (IsWaiting)
                await Task.Delay(1_000, token).ConfigureAwait(false);
        }

        bool match = false;
        if (list.Length != 0)
        {
            if (!huntedspecies || huntedspecies && Settings.OutbreakConditions.AlphaShinyOnly && !pk.IsAlpha)
            {
                url = TradeExtensions<PK9>.PokeImg(pk, false, false);
                EchoUtil.EchoEmbed("", print, url, "", false);
            }

            if (huntedspecies && Settings.OutbreakConditions.AlphaShinyOnly && pk.IsAlpha || huntedspecies && !Settings.OutbreakConditions.AlphaShinyOnly && pk.IsShiny)
            {
                url = TradeExtensions<PK9>.PokeImg(pk, false, false);
                print = Hub.Config.StopConditions.GetAlphaPrintName(pk);
                EchoUtil.EchoEmbed(Hub.Config.StopConditions.MatchFoundEchoMention, print, url, "", true);
                match = true;
            }
        }

        if (list.Length == 0 && !Settings.OutbreakConditions.CheckBoxes)
        {
            if (Settings.OutbreakConditions.AlphaShinyOnly && !pk.IsAlpha)
            {
                url = TradeExtensions<PK9>.PokeImg(pk, false, false);
                EchoUtil.EchoEmbed("", print, url, "", false);
            }
            else
            {
                url = TradeExtensions<PK9>.PokeImg(pk, false, false);
                EchoUtil.EchoEmbed(Hub.Config.StopConditions.MatchFoundEchoMention, print, url, "", true);
                match = true;
            }
        }

        if (Settings.OutbreakConditions.CheckBoxes && match == false)
        {
            int f = 0;
            int n = 0;
            foreach (var mon in boxlist.ToList())
            {
                bool alpha = !mon.IsAlpha && pk.IsAlpha && mon.Species == pk.Species;
                bool newform = mon.Form != pk.Form && alpha;
                if (alpha || newform || !mon.IsAlpha && pk.IsAlpha && (Species)pk.Species == Species.Eevee || !mon.IsAlpha && pk.IsAlpha && (Species)pk.Species == Species.Unown)
                {
                    if (f == 0)
                    {
                        Log($"Found a {(Species)pk.Species}! It's something we don't have!\nAdding it to our boxlist!");
                        url = TradeExtensions<PK9>.PokeImg(pk, false, false);
                        EchoUtil.EchoEmbed(Hub.Config.StopConditions.MatchFoundEchoMention, print, url, "", true);
                        match = true;
                        boxlist.Add(pk);
                    }
                    f++;
                }
                else
                {
                    if (n == 0)
                    {
                        url = TradeExtensions<PK9>.PokeImg(pk, false, false);
                        EchoUtil.EchoEmbed("", print, url, "", false);
                        n++;
                    }
                }
            }
        }

        if (match)
        {
            IsWaiting = true;
            IsWaitingConfirmation = true;
            Settings.AddCompletedShinyAlphaFound();
            Log($"Match found! Enter{map}, type $continue or click the Continue button and I'll teleport you to the location of {(Species)pk.Species} in a Massive Mass Outbreak!");
            Log($"Match found! Enter{map}, type $continue or click the Continue button and I'll teleport you to the location of {(Species)pk.Species} in a Massive Mass Outbreak!");
            while (IsWaiting)
            {
                await Task.Delay(1_000, token).ConfigureAwait(false);
                if (!IsWaitingConfirmation)
                {
                    if (Settings.SpecialConditions.UsePlayerInvincibleCheat)
                    {
                        await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0xD65F03C0), MainNsoBase + InvincibleTrainer1, token).ConfigureAwait(false);
                        await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(0xD65F03C0), MainNsoBase + InvincibleTrainer2, token).ConfigureAwait(false);
                    }

                    await TeleportToMMOGroupZone(token).ConfigureAwait(false);
                    await Click(HOME, 1_000, token).ConfigureAwait(false);
                    Log($"Teleported to the location of {(Species)pk.Species}! Pressing HOME incase you weren't ready in game.");
                    while (IsWaiting)
                        await Task.Delay(1_000, token).ConfigureAwait(false);
                }
            }
        }
    }

    private static void SetFakeTable(SlotDetail[] slots, ulong key)
    {
        foreach (var s in slots)
            s.SetSpecies();

        SpawnGenerator.EncounterTables.Add(key, slots);
    }

    private async Task PerformMultiSpawnerScan(PokedexSaveData dex, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var task = Hub.Config.ArceusLA.MultiScanConditions.MultiSpecies switch
            {
                MultiSpawners.Eevee => PerformMultiEeveeScan(token),
                MultiSpawners.CombeeLeft or MultiSpawners.CombeeRight => PerformMultiCombeeScan(token),
                //MultiSpawners.Unown => PerformMultiUnownScan(token),
                MultiSpawners.BasculinLeft or MultiSpawners.BasculinMid or MultiSpawners.BasculinRight => PerformMultiBasculinScan(token),
                MultiSpawners.HipposRight or MultiSpawners.HipposLeft => PerformMultiHippoScan(token),
                MultiSpawners.Magikarp => PerformMultiMagikarpScan(token),
                MultiSpawners.Buneary => PerformMultiBunnyScan(token),
                MultiSpawners.TreeHunt => PerformTreeHunt(dex, token),
                _ => PerformMultiEeveeScan(token),
            };
            await task.ConfigureAwait(false);
        }
    }
    private async Task PerformMultiEeveeScan(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            int count = 2;
            ulong key = (ulong)(0x1337BABE12345678 + Util.Rand.Next(1, 999999999));
            var slots = new SlotDetail[]
            {
                new(100, "Bidoof", false, [3, 6], 0),
                new(2, "Bidoof", true , [17, 19], 3),
                new(20, "Eevee", false, [3, 6], 0),
                new(1, "Eevee", true , [17, 19], 3),
            };
            SetFakeTable(slots, key);

            string log = string.Empty;
            var groupID = (int)Settings.MultiScanConditions.MultiSpecies;
            var ofs = new long[] { 0x42A6EE0, 0x330, 0x70 + groupID * 0x440 + 0x20 };
            var multiptr = await SwitchConnection.PointerAll(ofs, token).ConfigureAwait(false);
            var GeneratorSeed = BitConverter.ToUInt64(await SwitchConnection.ReadBytesAbsoluteAsync(multiptr, 8, token).ConfigureAwait(false), 0);
            var group_seed = (GeneratorSeed - 0x82A2B175229D6A5B) & 0xFFFFFFFFFFFFFFFF;
            //Log($"Seed: {group_seed:X16}");

            var details = new SpawnCount(count, count);
            var set = new SpawnSet(key, count);
            var spawner = SpawnInfo.GetLoop(details, set, SpawnType.Regular);

            var results = Permuter.Permute(spawner, group_seed, Settings.MultiScanConditions.Advances);
            if (!results.HasResults)
                log += $"\nNo results found within {Settings.MultiScanConditions.Advances} advances :(";
            else
            {
                var lines = results.GetLines();
                foreach (var line in lines)
                    log += "\n" + line;
            }
            Log($"{log}");
            IsWaiting = true;
            while (IsWaiting)
                await Task.Delay(1_000, token).ConfigureAwait(false);
        }
    }
    private async Task PerformMultiCombeeScan(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            int count = 2;
            ulong key = (ulong)(0x1337BABECAFEDEAD + Util.Rand.Next(1, 999999999));
            var slots = new SlotDetail[]
            {
                new(100, "Combee", false, [17, 20], 0),
                new(2, "Combee", true , [32, 35], 3),
            };
            SetFakeTable(slots, key);

            string log = string.Empty;
            var groupID = (int)Settings.MultiScanConditions.MultiSpecies;
            var ofs = new long[] { 0x42A6EE0, 0x330, 0x70 + groupID * 0x440 + 0x20 };
            var multiptr = await SwitchConnection.PointerAll(ofs, token).ConfigureAwait(false);
            var GeneratorSeed = BitConverter.ToUInt64(await SwitchConnection.ReadBytesAbsoluteAsync(multiptr, 8, token).ConfigureAwait(false), 0);
            var group_seed = (GeneratorSeed - 0x82A2B175229D6A5B) & 0xFFFFFFFFFFFFFFFF;
            //Log($"Seed: {group_seed:X16}");

            var details = new SpawnCount(count, count);
            var set = new SpawnSet(key, count);
            var spawner = SpawnInfo.GetLoop(details, set, SpawnType.Regular);

            var results = Permuter.Permute(spawner, group_seed, Settings.MultiScanConditions.Advances);
            if (!results.HasResults)
                log += $"\nNo results found within {Settings.MultiScanConditions.Advances} advances :(";
            else
            {
                var lines = results.GetLines();
                foreach (var line in lines)
                    log += "\n" + line;
            }
            Log($"{log}");
            IsWaiting = true;
            while (IsWaiting)
                await Task.Delay(1_000, token).ConfigureAwait(false);
        }
    }

    private readonly record struct TreeFinderTree(ulong Hash, string Table, string Id, float X, float Y, float Z, string Place, bool Burmy);

    private static readonly TreeFinderTree[] TreeCrimsonTrees = new TreeFinderTree[]
    {
            new(0x3245981E7057ABFCUL, "a02:gmk_tree_01_00", "0101", 288.7897f, 56.74902f, 369.92f, "in the Golden Lowlands", true),
            new(0x1C3B702966C39FBDUL, "a02:gmk_tree_01_00", "0102", 329.28f, 51.27916f, 464.45f, "in the Golden Lowlands", true),
            new(0x2E1495F6056D5F32UL, "a02:gmk_tree_01_00", "0103", 397.03f, 50.11192f, 500.1f, "in the Golden Lowlands", true),
            new(0xDBD5D4FF7A6158CEUL, "a02:gmk_tree_01_00", "0106", 353.86f, 53.19769f, 385.24f, "in the Golden Lowlands", true),
            new(0xF29B90BCAF87D8B4UL, "a02:gmk_tree_01_00", "0113", 340.46f, 49.78142f, 521.64f, "in the Golden Lowlands", true),
            new(0x389B6E3F39C34D4EUL, "a02:gmk_tree_01_00", "0116", 404.0402f, 52.26971f, 466.6338f, "in the Golden Lowlands", true),
            new(0x44DEEB681F4A9840UL, "a02:gmk_tree_02_00", "0201", 389.57f, 38.77f, 645.19f, "at Gapejaw Bog", true),
            new(0x72DB27036DF1D2D5UL, "a02:gmk_tree_03_00", "0301", 245.8f, 41.31178f, 579.9f, "at Gapejaw Bog", true),
            new(0x97190A5BAA525FD1UL, "a02:gmk_tree_03_00", "0302", 327.42f, 41.33623f, 693.2f, "at Gapejaw Bog", true),
            new(0xCDA14D6610F1DA74UL, "a02:gmk_tree_03_00", "0303", 244.29f, 41.42f, 618.16f, "at Gapejaw Bog", true),
            new(0x1ECCAEA048D402CBUL, "a02:gmk_tree_03_00", "0305", 295.47f, 39.55614f, 734.49f, "at Gapejaw Bog", true),
            new(0xDA89D8C9603B316BUL, "a02:gmk_tree_03_00", "0307", 244.1f, 49.64565f, 516.1f, "in the Crimson Mirelands", true),
            new(0xC45B0FA795ED2376UL, "a02:gmk_tree_03_00", "0308", 266.3f, 54.30385f, 510.9f, "in the Crimson Mirelands", true),
            new(0x1C7565276BF208D1UL, "a02:gmk_tree_03_00", "0309", 228.76f, 47.39825f, 544.98f, "in the Crimson Mirelands", true),
            new(0x7F4587D1641B521FUL, "a02:gmk_tree_03_00", "0321", 266.24f, 41.34f, 679.65f, "at Gapejaw Bog", true),
            new(0x49B1A4F9D54A673DUL, "a02:gmk_tree_04_00", "0401", 346.92f, 42.15102f, 718.45f, "at Gapejaw Bog", true),
            new(0x22EEC31E3087CA47UL, "a02:gmk_tree_04_00", "0402", 375.24f, 39.71f, 731.66f, "at Gapejaw Bog", true),
            new(0x8E2F4E574D2E0532UL, "a02:gmk_tree_04_00", "0403", 374.13f, 40.43439f, 715.05f, "at Gapejaw Bog", true),
            new(0xCD25962D468D8AACUL, "a02:gmk_tree_04_00", "0450", 408.96f, 38.07982f, 701.1f, "at Gapejaw Bog", true),
            new(0x74035094BE0F0149UL, "a02:gmk_tree_0501_00", "0501_01", 511.3492f, 42.4118f, 659.7872f, "in the Crimson Mirelands", true),
            new(0x5D630E842C18BB95UL, "a02:gmk_tree_0502_00", "0502_01", 469.33f, 51.64153f, 488.84f, "in the Crimson Mirelands", true),
            new(0x1F123AA14159881AUL, "a02:gmk_tree_06_00", "0604", 551.43f, 83.79079f, 440.9f, "in the Crimson Mirelands", true),
            new(0xE8B47DA6050E6DCBUL, "a02:gmk_tree_06_00", "0605", 612.22f, 73.907f, 416.44f, "in the Crimson Mirelands", true),
            new(0xC1394C8AB7FE8AFDUL, "a02:gmk_tree_06_00", "0606", 494.76f, 73.49341f, 420.96f, "in the Crimson Mirelands", true),
            new(0x5430582E2D673971UL, "a02:gmk_tree_06_00", "0607", 573.1f, 75.93127f, 392.36f, "in the Crimson Mirelands", true),
            new(0x3D230F5CFF74FB2CUL, "a02:gmk_tree_06_00", "0608", 508.99f, 75.77899f, 394.23f, "in the Crimson Mirelands", true),
            new(0xD0441441EDBE4CA2UL, "a02:gmk_tree_06_00", "0609", 573.2072f, 83.59524f, 422.2368f, "in the Crimson Mirelands", true),
            new(0x9DACA93262B513B2UL, "a02:gmk_tree_06_00", "0610", 561.07f, 78.07129f, 370.78f, "in the Crimson Mirelands", true),
            new(0xEA9D0411C048F6DAUL, "a02:gmk_tree_06_00", "0611", 546.59f, 59.44765f, 479.88f, "in the Crimson Mirelands", true),
            new(0x2C8A7CA8D96F1126UL, "a02:gmk_tree_06_00", "0614", 493.94f, 63.67999f, 459.9f, "in the Crimson Mirelands", true),
            new(0x314102343EA21A2EUL, "a02:gmk_tree_06_00", "0621", 668.27f, 78.16849f, 435.72f, "in the Crimson Mirelands", true),
            new(0x05A672C36448968AUL, "a02:gmk_tree_06_00", "0619", 663.45f, 86.33306f, 386.3f, "in the Crimson Mirelands", true),
            new(0xE2C2CE1C50BF642CUL, "a02:gmk_tree_06_00", "0650", 684.5305f, 78.21738f, 419.2326f, "in the Crimson Mirelands", true),
            new(0x7C1AF41B21FDCA7CUL, "a02:gmk_tree_06_00", "0623", 521.2f, 74.03119f, 418.13f, "in the Crimson Mirelands", true),
            new(0x97BD0EA10160C13DUL, "a02:gmk_tree_06_00", "0601", 609.16f, 57.93669f, 509.26f, "on Bolderoll Slope", true),
            new(0x3814857281F40E9CUL, "a02:gmk_tree_06_00", "0651", 660.2f, 86.16039f, 408.97f, "in the Crimson Mirelands", true),
            new(0x551D0EE09B2EBC65UL, "a02:gmk_tree_07_00", "0701", 485.52f, 84.17738f, 338.32f, "in the Crimson Mirelands", true),
            new(0xB20E834AC42CCF99UL, "a02:gmk_tree_08_00", "0801", 455.41f, 92.43f, 289.47f, "in the Crimson Mirelands", true),
            new(0x1BAAF9C650C4C6A5UL, "a02:gmk_tree_08_00", "0802", 425.69f, 96.89f, 263.86f, "in the Crimson Mirelands", true),
            new(0x561CEEC28C14694DUL, "a02:gmk_tree_08_00", "0803", 397.5f, 96.54f, 293.7f, "in the Crimson Mirelands", true),
            new(0xC3E0E2DF7D921E26UL, "a02:gmk_tree_08_00", "0804", 394.47f, 99.14037f, 206.72f, "on Cloudpool Ridge", true),
            new(0x639AE584D238CC4FUL, "a02:gmk_tree_08_00", "0805", 360.65f, 96.45322f, 300.22f, "in the Crimson Mirelands", true),
            new(0xC328932434A5998FUL, "a02:gmk_tree_08_00", "0806", 376.77f, 97.08879f, 267.98f, "in the Crimson Mirelands", true),
            new(0x2349FF8EE37B44C3UL, "a02:gmk_tree_08_00", "0807", 470.91f, 103.55f, 212.43f, "in the Crimson Mirelands", true),
            new(0x09DEDFA35C87410BUL, "a02:gmk_tree_08_00", "0808", 337.47f, 111.7557f, 124.4f, "on Cloudpool Ridge", true),
            new(0x9FEC32EBC16AB545UL, "a02:gmk_tree_08_00", "0809", 340.18f, 104.8101f, 210.62f, "on Cloudpool Ridge", true),
            new(0xAEE439D1F8CBC12AUL, "a02:gmk_tree_08_00", "0812", 399.3f, 100.3f, 182.9f, "on Cloudpool Ridge", true),
            new(0x440E9E9A196F3BF3UL, "a02:gmk_tree_08_00", "0819", 480.57f, 97.04938f, 232.63f, "in the Crimson Mirelands", true),
            new(0x82326190BE26B5A8UL, "a02:gmk_tree_08_00", "0824", 438.4f, 91.56425f, 325.76f, "in the Crimson Mirelands", true),
            new(0x607CEC626ED3E34FUL, "a02:gmk_tree_08_00", "0826", 400.75f, 96.53f, 271.09f, "in the Crimson Mirelands", true),
            new(0xFF20FB774359413CUL, "a02:gmk_tree_08_00", "0827", 448.81f, 101.89f, 217.88f, "in the Crimson Mirelands", true),
            new(0x70D96310558AECD1UL, "a02:gmk_tree_08_00", "0853", 372.5922f, 123.49f, 90.35976f, "on Cloudpool Ridge", true),
            new(0xBFBA84612CAF56F8UL, "a02:gmk_tree_08_00", "0854", 356.5342f, 123.587f, 106.5416f, "on Cloudpool Ridge", true),
            new(0xF781D746A8B09FEAUL, "a02:gmk_tree_08_00", "0859", 453.86f, 132.4849f, 83.52f, "in the Crimson Mirelands", true),
            new(0x966C9BC6BC6E2E4BUL, "a02:gmk_tree_09_00", "0905", 625.88f, 120.0792f, 173.83f, "at the Shrouded Ruins", true),
            new(0x3DC075663996B799UL, "a02:gmk_tree_09_00", "0906", 569.75f, 121.1608f, 209.28f, "at the Shrouded Ruins", true),
            new(0xD773342BAA18307EUL, "a02:gmk_tree_09_00", "0953", 634.0495f, 120.0792f, 156.2893f, "at the Shrouded Ruins", true),
            new(0xDEA950FE64785279UL, "a02:gmk_tree_10_00", "1001", 473.76f, 48.3972f, 684.85f, "in the Crimson Mirelands", true),
            new(0x7C272B54B43D876BUL, "a02:gmk_tree_10_00", "1003", 533.44f, 47.6442f, 724.77f, "in the Crimson Mirelands", true),
            new(0xF53A5173023C11EAUL, "a02:gmk_tree_10_00", "1006", 414.06f, 54.60423f, 605.18f, "in the Crimson Mirelands", true),
            new(0x769041898183D680UL, "a02:gmk_tree_10_00", "1007", 448.6f, 49.649f, 627.3f, "in the Crimson Mirelands", true),
            new(0x778E25EC798162CEUL, "a02:gmk_tree_10_00", "1008", 419.31f, 57.11f, 494.03f, "in the Crimson Mirelands", true),
            new(0xA57E239E63E2CE2CUL, "a02:gmk_tree_10_00", "1009", 442.01f, 59.37743f, 535.31f, "in the Crimson Mirelands", true),
            new(0x14560420FFE366FAUL, "a02:gmk_tree_10_00", "1010", 408.77f, 49.28329f, 564.24f, "in the Crimson Mirelands", true),
            new(0xD86EEB8D6C67D472UL, "a02:gmk_tree_10_00", "1011", 595.54f, 54.8681f, 683.4f, "in the Crimson Mirelands", true),
            new(0x545BAC5182FA5C21UL, "a02:gmk_tree_12_00", "1201", 661.49f, 86.30993f, 218.91f, "in the Crimson Mirelands", true),
            new(0x6F511B7A979EC90AUL, "a02:gmk_tree_12_00", "1202", 818.07f, 86.59594f, 190.87f, "in the Crimson Mirelands", true),
            new(0xEEB894A9D92ED8CEUL, "a02:gmk_tree_12_00", "1203", 853.23f, 90.3235f, 288.4f, "in the Crimson Mirelands", true),
            new(0xC7CC266D4680326DUL, "a02:gmk_tree_12_00", "1204", 803.51f, 66.80011f, 472.3f, "in the Crimson Mirelands", true),
            new(0xF62E3B63C5EE95BDUL, "a02:gmk_tree_12_00", "1207", 859.75f, 83.82135f, 360.0927f, "in the Crimson Mirelands", true),
            new(0x34F2D3AE504753A1UL, "a02:gmk_tree_12_00", "1215", 859.5f, 61.13612f, 518.3f, "in the Crimson Mirelands", true),
            new(0x3C49D3F3D18D2228UL, "a02:gmk_tree_12_00", "1216", 771.3f, 72.1245f, 431.81f, "in the Crimson Mirelands", true),
            new(0x58322C0D0B9D65A4UL, "a02:gmk_tree_12_00", "1217", 852.28f, 78.57529f, 400.23f, "in the Crimson Mirelands", true),
            new(0x7999B9CBF2A566BCUL, "a02:gmk_tree_12_00", "1218", 736.53f, 66.5531f, 480.08f, "in the Crimson Mirelands", true),
            new(0xA0C194349B663B9EUL, "a02:gmk_tree_12_00", "1253", 845.1458f, 67.62054f, 476.3436f, "in the Crimson Mirelands", true),
            new(0x823F0DE4249BEDFFUL, "a02:gmk_tree_12_00", "1255", 742.7213f, 56.87126f, 511.689f, "in the Crimson Mirelands", true),
            new(0x9CF9B3C26675B2D0UL, "a02:gmk_tree_14_00", "1401", 842.28f, 34.75983f, 828.87f, "in the Crimson Mirelands", true),
            new(0x9F4A0539C7A77580UL, "a02:gmk_tree_14_00", "1403", 881.41f, 34.49409f, 832.66f, "in the Crimson Mirelands", true),
            new(0x2F763AB1B0E10756UL, "a02:gmk_tree_15_00", "1502", 663.1f, 53.86974f, 722.6f, "in the Crimson Mirelands", true),
            new(0x2EBBAD3BCC9E3446UL, "a02:gmk_tree_15_00", "1504", 696.13f, 38.02087f, 750.1f, "in the Crimson Mirelands", true),
            new(0x850BDBE2720DE03DUL, "a02:gmk_tree_15_00", "1505", 766.28f, 37.98727f, 771.29f, "in the Crimson Mirelands", true),
            new(0x0274F1A5587F01D7UL, "a02:gmk_tree_15_00", "1506", 716.02f, 36.50226f, 712.02f, "in the Crimson Mirelands", true),
            new(0x0F9BF093C161B0B6UL, "a02:gmk_tree_15_00", "1507", 695.36f, 38.36325f, 677.6f, "in the Crimson Mirelands", true),
            new(0x9D611209817E0760UL, "a02:gmk_tree_15_00", "1510", 697.8f, 36.99937f, 796.6f, "in the Crimson Mirelands", true),
            new(0x0255AC32B5121D90UL, "a02:gmk_tree_15_00", "1550", 642.4655f, 53.19821f, 760.8637f, "in the Crimson Mirelands", true),
            new(0x338DCC83456815F7UL, "a02:gmk_tree_16_00", "1601", 690.85f, 35.32278f, 827.4f, "in the Crimson Mirelands", true),
            new(0x6884D04D3C473865UL, "a02:gmk_tree_16_00", "1609", 728.2f, 34.90823f, 923.9f, "at Ursa’s Ring", true),
            new(0xA3481F70849B50BDUL, "a02:gmk_tree_16_00", "1651", 769.8145f, 35.15031f, 846.2183f, "at Ursa’s Ring", true),
            new(0xED6714BD344E244AUL, "a02:gmk_tree_16_00", "1656", 695.33f, 37.86079f, 891.26f, "at Ursa’s Ring", true),
            new(0xC30424FC827C6D6AUL, "a02:gmk_tree_17_00", "1702", 434.41f, 37.1593f, 811.87f, "at the Holm of Trials", true),
            new(0xF82380FDBAC87DD3UL, "a02:gmk_tree_17_00", "1703", 353.7f, 37.03278f, 785.5f, "at the Holm of Trials", true),
            new(0xE81312C9B21F3140UL, "a02:gmk_tree_17_00", "1704", 300.8f, 42.17178f, 810.7f, "at the Holm of Trials", true),
            new(0x16C3CB47569186C2UL, "a02:gmk_tree_17_00", "1705", 338.06f, 44.24863f, 766.56f, "at the Holm of Trials", true),
            new(0x72013AEDFF239EB8UL, "a02:gmk_tree_17_00", "1706", 367.11f, 36.91171f, 876.2f, "at the Holm of Trials", true),
            new(0xB819187330E0AC11UL, "a02:gmk_tree_17_00", "1707", 354.07f, 37.78291f, 913.31f, "at the Holm of Trials", true),
            new(0xA12C505F7C907B55UL, "a02:gmk_tree_17_00", "1727", 452.7f, 36.13347f, 929.8f, "at the Holm of Trials", true),
            new(0x5BF241098B83EC34UL, "a02:gmk_tree_17_00", "1730", 330.08f, 40.15065f, 824.57f, "at the Holm of Trials", true),
            new(0x2751683E5E196FBCUL, "a02:gmk_tree_18_00", "1801", 615.3f, 38.44832f, 901.81f, "at the Holm of Trials", true),
            new(0xEB918288801065CCUL, "a02:gmk_tree_18_00", "1802", 498.5f, 37.9635f, 867.62f, "at the Holm of Trials", true),
            new(0x1DB1DB2E4AF042FCUL, "a02:gmk_tree_18_00", "1803", 547.07f, 38.464f, 913.14f, "at the Holm of Trials", true),
            new(0x984EE56B227946ECUL, "a02:gmk_tree_18_00", "1818", 572.97f, 38.0894f, 895.84f, "at the Holm of Trials", true),
            new(0x79F0CEF16739356DUL, "a02:gmk_tree_19_00", "1902", 220.94f, 70.40726f, 316.68f, "in the Crimson Mirelands", true),
            new(0x5EB2734A17102559UL, "a02:gmk_tree_20_00", "2001", 169.01f, 50.02344f, 706.8f, "in the Crimson Mirelands", true),
            new(0xD4CE4CBBB612CBE0UL, "a02:gmk_tree_20_00", "2002", 181.3f, 50.14417f, 609.17f, "in the Crimson Mirelands", true),
            new(0xED32C6428C65166CUL, "a02:gmk_tree_20_00", "2003", 255.69f, 50.98892f, 714.04f, "in the Crimson Mirelands", true),
            new(0x5E6BA88B12364EAEUL, "a02:gmk_tree_20_00", "2007", 200.52f, 49.46576f, 676.83f, "in the Crimson Mirelands", true),
            new(0xA8A89F9CBDE89A47UL, "a02:gmk_tree_20_00", "2008", 173.13f, 49.57761f, 654.71f, "in the Crimson Mirelands", true),
            new(0xC5A6ECE9AF1C43F6UL, "a02:gmk_tree_20_00", "2010", 235.83f, 50.86842f, 751.13f, "in the Crimson Mirelands", true),
    };

    private readonly record struct TreeAreaSlot(ushort Species, bool Alpha, string Name, int Weight, int LevelBase);

    private static readonly Dictionary<string, TreeAreaSlot[]> TreeAreaSlots = new()
    {
        ["a02:gmk_tree_01_00"] = new TreeAreaSlot[]
        {
            new((ushort)Species.Burmy, false, "Burmy-1", 100, 16),
            new((ushort)Species.Burmy, true, "alpha Burmy-1", 1, 31),
        },
        ["a02:gmk_tree_02_00"] = new TreeAreaSlot[]
        {
            new((ushort)Species.Burmy, false, "Burmy-1", 100, 16),
            new((ushort)Species.Burmy, true, "alpha Burmy-1", 1, 31),
        },
        ["a02:gmk_tree_03_00"] = new TreeAreaSlot[]
        {
            new((ushort)Species.Burmy, false, "Burmy-1", 100, 16),
            new((ushort)Species.Pachirisu, false, "Pachirisu", 20, 20),
            new((ushort)Species.Burmy, true, "alpha Burmy-1", 1, 31),
            new((ushort)Species.Pachirisu, true, "alpha Pachirisu", 1, 35),
        },
        ["a02:gmk_tree_04_00"] = new TreeAreaSlot[]
        {
            new((ushort)Species.Burmy, false, "Burmy-1", 100, 16),
            new((ushort)Species.Pachirisu, false, "Pachirisu", 20, 20),
            new((ushort)Species.Burmy, true, "alpha Burmy-1", 1, 31),
            new((ushort)Species.Pachirisu, true, "alpha Pachirisu", 1, 35),
        },
        ["a02:gmk_tree_0501_00"] = new TreeAreaSlot[]
        {
            new((ushort)Species.Burmy, false, "Burmy-1", 100, 16),
            new((ushort)Species.Burmy, true, "alpha Burmy-1", 1, 31),
        },
        ["a02:gmk_tree_0502_00"] = new TreeAreaSlot[]
        {
            new((ushort)Species.Burmy, false, "Burmy-1", 100, 16),
            new((ushort)Species.Burmy, true, "alpha Burmy-1", 1, 31),
        },
        ["a02:gmk_tree_06_00"] = new TreeAreaSlot[]
        {
            new((ushort)Species.Burmy, false, "Burmy-1", 100, 16),
            new((ushort)Species.Burmy, true, "alpha Burmy-1", 1, 31),
        },
        ["a02:gmk_tree_07_00"] = new TreeAreaSlot[]
        {
            new((ushort)Species.Burmy, false, "Burmy-1", 100, 16),
            new((ushort)Species.Burmy, true, "alpha Burmy-1", 1, 31),
        },
        ["a02:gmk_tree_08_00"] = new TreeAreaSlot[]
        {
            new((ushort)Species.Burmy, false, "Burmy-1", 100, 16),
            new((ushort)Species.Burmy, true, "alpha Burmy-1", 1, 31),
        },
        ["a02:gmk_tree_09_00"] = new TreeAreaSlot[]
        {
            new((ushort)Species.Burmy, false, "Burmy-1", 100, 16),
            new((ushort)Species.Burmy, true, "alpha Burmy-1", 1, 31),
        },
        ["a02:gmk_tree_10_00"] = new TreeAreaSlot[]
        {
            new((ushort)Species.Burmy, false, "Burmy-1", 100, 16),
            new((ushort)Species.Pachirisu, false, "Pachirisu", 20, 21),
            new((ushort)Species.Burmy, true, "alpha Burmy-1", 1, 31),
            new((ushort)Species.Pachirisu, true, "alpha Pachirisu", 1, 36),
        },
        ["a02:gmk_tree_12_00"] = new TreeAreaSlot[]
        {
            new((ushort)Species.Burmy, false, "Burmy-1", 100, 16),
            new((ushort)Species.Burmy, true, "alpha Burmy-1", 1, 31),
        },
        ["a02:gmk_tree_14_00"] = new TreeAreaSlot[]
        {
            new((ushort)Species.Burmy, false, "Burmy-1", 100, 16),
            new((ushort)Species.Pachirisu, false, "Pachirisu", 20, 33),
            new((ushort)Species.Burmy, true, "alpha Burmy-1", 1, 31),
            new((ushort)Species.Pachirisu, true, "alpha Pachirisu", 1, 48),
        },
        ["a02:gmk_tree_15_00"] = new TreeAreaSlot[]
        {
            new((ushort)Species.Burmy, false, "Burmy-1", 100, 16),
            new((ushort)Species.Pachirisu, false, "Pachirisu", 20, 27),
            new((ushort)Species.Burmy, true, "alpha Burmy-1", 1, 31),
            new((ushort)Species.Pachirisu, true, "alpha Pachirisu", 1, 42),
        },
        ["a02:gmk_tree_16_00"] = new TreeAreaSlot[]
        {
            new((ushort)Species.Burmy, false, "Burmy-1", 100, 16),
            new((ushort)Species.Pachirisu, false, "Pachirisu", 20, 27),
            new((ushort)Species.Burmy, true, "alpha Burmy-1", 1, 31),
            new((ushort)Species.Pachirisu, true, "alpha Pachirisu", 1, 42),
        },
        ["a02:gmk_tree_17_00"] = new TreeAreaSlot[]
        {
            new((ushort)Species.Burmy, false, "Burmy-1", 100, 16),
            new((ushort)Species.Pachirisu, false, "Pachirisu", 20, 33),
            new((ushort)Species.Burmy, true, "alpha Burmy-1", 1, 31),
            new((ushort)Species.Pachirisu, true, "alpha Pachirisu", 1, 48),
        },
        ["a02:gmk_tree_18_00"] = new TreeAreaSlot[]
        {
            new((ushort)Species.Burmy, false, "Burmy-1", 100, 16),
            new((ushort)Species.Pachirisu, false, "Pachirisu", 20, 33),
            new((ushort)Species.Burmy, true, "alpha Burmy-1", 1, 31),
            new((ushort)Species.Pachirisu, true, "alpha Pachirisu", 1, 48),
        },
        ["a02:gmk_tree_19_00"] = new TreeAreaSlot[]
        {
            new((ushort)Species.Burmy, false, "Burmy-1", 100, 16),
            new((ushort)Species.Wormadam, false, "Wormadam-1", 20, 41),
            new((ushort)Species.Pachirisu, false, "Pachirisu", 20, 41),
            new((ushort)Species.Burmy, true, "alpha Burmy-1", 1, 31),
            new((ushort)Species.Wormadam, true, "alpha Wormadam-1", 1, 56),
            new((ushort)Species.Pachirisu, true, "alpha Pachirisu", 1, 56),
        },
        ["a02:gmk_tree_20_00"] = new TreeAreaSlot[]
        {
            new((ushort)Species.Burmy, false, "Burmy-1", 100, 16),
            new((ushort)Species.Wormadam, false, "Wormadam-1", 20, 41),
            new((ushort)Species.Pachirisu, false, "Pachirisu", 20, 41),
            new((ushort)Species.Burmy, true, "alpha Burmy-1", 1, 31),
            new((ushort)Species.Wormadam, true, "alpha Wormadam-1", 1, 56),
            new((ushort)Species.Pachirisu, true, "alpha Pachirisu", 1, 56),
        },
    };

    private async Task<byte[]?> TreeFinderTryRead(ulong address, int length, CancellationToken token)
    {
        if (address == 0)
            return null;
        try
        {
            return await SwitchConnection.ReadBytesAbsoluteAsync(address, length, token).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private const long TreeSaveRoot = 0x42BA6B0;
    private const long TreeSaveTableField = 0x220;

    private sealed class TreeSaveEntry
    {
        public TreeFinderTree Tree;
        public int Offset;
        public ulong Value;
    }

    private sealed class TreeSavePrediction
    {
        public string Name = string.Empty;
        public ushort Species;
        public bool Alpha;
        public int Level;
        public double Slot;
        public int Draws;
        public ulong FixedSeed;
        public bool Shiny;
        public uint EC, PID;
        public int Nature, Gender;
        public int[] IVs = new int[6];
        public bool Ambiguous;
        public bool Alternative;
        public int ShakeScore;
        public bool LikelyShaking;
    }

    private static List<TreeSaveEntry> TreeSaveParse(byte[] data)
    {
        var byHash = TreeCrimsonTrees.GroupBy(t => t.Hash).ToDictionary(g => g.Key, g => g.First());
        var list = new List<TreeSaveEntry>();
        for (int o = 0; o + 16 <= data.Length; o += 8)
        {
            ulong q = BitConverter.ToUInt64(data, o);
            if (q != 0 && byHash.TryGetValue(q, out var tree))
                list.Add(new TreeSaveEntry { Tree = tree, Offset = o, Value = BitConverter.ToUInt64(data, o + 8) });
        }
        return list;
    }

    private static Dictionary<ulong, ulong> TreeSaveValueMap(IEnumerable<TreeSaveEntry> entries)
    {
        var map = new Dictionary<ulong, ulong>();
        foreach (var e in entries)
            map.TryAdd(e.Tree.Hash, e.Value);
        return map;
    }

    private static List<string> TreeSaveDuplicateReport(IEnumerable<TreeSaveEntry> entries)
        => entries.GroupBy(e => e.Tree.Hash).Where(g => g.Count() > 1)
            .Select(g => $"landmark {g.First().Tree.Id} ({g.First().Tree.Table}) appears {g.Count()} times: " +
                         string.Join("; ", g.Select(e => $"offset +0x{e.Offset:X} value {e.Value:X16}"))).ToList();

    private (bool Shiny, uint EC, uint PID, int Nature, int Gender, int[] IVs) TreeSaveMake(PokedexSaveData dex, ushort species, bool alpha, ulong fixedSeed, int rollsOverride = -1)
    {
        int gt = PersonalTable.LA.GetFormEntry(species, 0).Gender;
        var (perfect, complete) = CheckForPerfectComplete(HasCharm, dex, species);
        int rolls = rollsOverride > 0 ? rollsOverride : 1 + (complete ? 1 : 0) + (perfect ? 2 : 0) + (HasCharm ? 3 : 0) + (int)(SpawnType.Regular - 7);
        var gen = GenerateFromSeed(fixedSeed, rolls, alpha ? 3 : 0, gt);
        return (gen.shiny, gen.EC, gen.PID, gen.nature, gen.gender, gen.IVs);
    }

    private TreeSavePrediction TreeSavePredictFull(PokedexSaveData dex, string table, ulong value, int rollsOverride = -1, int variant = 0)
    {
        var rng = new Xoroshiro128Plus(value);
        int draws = 0;
        int shakeScore = 127;
        while (draws < 64)
        {
            ulong dr = rng.Next();
            draws++;
            shakeScore = (int)(dr & 0x7F);
            if (shakeScore < 100)
                break;
        }
        ulong slotDraw = rng.Next();
        draws++;
        var areaSlots = TreeAreaSlots[table];
        int total = areaSlots.Sum(sl => sl.Weight);
        double slot = slotDraw / Math.Pow(2, 64) * total;
        var chosen = areaSlots[^1];
        double acc = 0;
        foreach (var sl in areaSlots)
        {
            acc += sl.Weight;
            if (slot < acc)
            {
                chosen = sl;
                break;
            }
        }
        var pick = (chosen.Species, chosen.Alpha, chosen.Name);
        int areaLevelBase = chosen.LevelBase;
        if (pick.Alpha && !Settings.TreeHuntConditions.AlphaSlotsSpawnAlpha)
            pick = (pick.Species, false, pick.Name.Replace("alpha ", ""));
        ulong fixedSeed = rng.Next();
        draws++;
        int level = areaLevelBase + (int)(rng.Next() & 3);
        var gen = TreeSaveMake(dex, pick.Species, pick.Alpha, fixedSeed, rollsOverride);
        return new TreeSavePrediction
        {
            Name = pick.Name, Species = pick.Species, Alpha = pick.Alpha, Level = level, Slot = slot, Draws = draws, FixedSeed = fixedSeed,
            Shiny = gen.Shiny, EC = gen.EC, PID = gen.PID, Nature = gen.Nature, Gender = gen.Gender, IVs = gen.IVs,
            Ambiguous = false, Alternative = variant == 1, ShakeScore = shakeScore, LikelyShaking = shakeScore < 10,
        };
    }

    private static string TreeSaveDescribe(TreeSavePrediction p)
        => $"{(p.Shiny ? "SHINY " : string.Empty)}{p.Name} lv {p.Level}, nature {(Nature)p.Nature}, {(p.Gender == 0 ? "male" : p.Gender == 1 ? "female" : "genderless")}, IVs (HP/Atk/Def/SpA/SpD/Spe) {string.Join("/", p.IVs)}, EC {p.EC:X8}, PID {p.PID:X8}, shake score {p.ShakeScore}{(p.LikelyShaking ? " (LIKELY SHAKING)" : string.Empty)}";

    private static string TreeSaveDescribe((bool Shiny, uint EC, uint PID, int Nature, int Gender, int[] IVs) g)
        => $"{(g.Shiny ? "SHINY " : string.Empty)}nature {(Nature)g.Nature}, {(g.Gender == 0 ? "male" : g.Gender == 1 ? "female" : "genderless")}, EC {g.EC:X8}, PID {g.PID:X8}";

    private int treeHuntPollMs = 2_000;

    private async Task<(ulong Address, byte[] Data, List<TreeSaveEntry> Entries)?> TreeHuntReadTable(CancellationToken token)
    {
        var addr = await SwitchConnection.PointerAll(new long[] { TreeSaveRoot, TreeSaveTableField, 0 }, token).ConfigureAwait(false);
        if (addr == 0)
            return null;
        int size = 0x6000;
        var head = await TreeFinderTryRead(addr, 0x20, token).ConfigureAwait(false);
        if (head != null)
        {
            ulong self = BitConverter.ToUInt64(head, 0x10), objSize = BitConverter.ToUInt64(head, 0x18);
            if (self == addr && objSize >= 0x100 && objSize <= 0x40000)
                size = (int)objSize;
        }
        var data = await TreeFinderTryRead(addr, size, token).ConfigureAwait(false);
        if (data == null)
            return null;
        var entries = TreeSaveParse(data);
        int found = entries.Where(e => e.Tree.Burmy).Select(e => e.Tree.Hash).Distinct().Count();
        treeHuntLastFound = found;
        treeHuntLastExpected = TreeCrimsonTrees.Length;
        return found > 0 ? (addr, data, entries) : null;
    }

    private int treeHuntLastFound, treeHuntLastExpected;

    private double treeHuntLastRestartSeconds;

    private async Task<(List<TreeSaveEntry>? Entries, double Seconds)> TreeHuntPressAndWait(Dictionary<ulong, ulong>? baseline, int presses, bool stopEarly, int timeoutSeconds, CancellationToken token)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        Dictionary<ulong, ulong>? last = null;
        int pressed = 0, stable = 0;
        while (DateTime.UtcNow < deadline)
        {
            if (pressed < presses)
            {
                await Click(A, 1_000, token).ConfigureAwait(false);
                pressed++;
            }
            else
                await Task.Delay(stopEarly ? 1_000 : treeHuntPollMs, token).ConfigureAwait(false);

            if (pressed < presses && !(stopEarly && baseline != null))
                continue;

            var table = await TreeHuntReadTable(token).ConfigureAwait(false);
            if (table == null)
            {
                last = null;
                stable = 0;
                continue;
            }
            var now = TreeSaveValueMap(table.Value.Entries.Where(e => e.Tree.Burmy));
            bool isFresh = baseline == null || now.Count(kv => !baseline.TryGetValue(kv.Key, out var old) || old != kv.Value) >= 10;
            if (isFresh && last != null && last.Count == now.Count && now.All(kv => last.TryGetValue(kv.Key, out var prev) && prev == kv.Value))
            {
                stable++;
                if (stable >= (stopEarly ? 2 : 1))
                    return (table.Value.Entries, sw.Elapsed.TotalSeconds);
            }
            else
                stable = 0;
            last = isFresh ? now : null;
        }
        return (null, sw.Elapsed.TotalSeconds);
    }

    private async Task<bool> TreeHuntRestartGame(int watchdogMinutes, Action<string> say, CancellationToken token)
    {
        if (watchdogMinutes <= 0)
        {
            await CloseGame(Hub.Config, token).ConfigureAwait(false);
            await StartGame(Hub.Config, token).ConfigureAwait(false);
            return true;
        }

        var restartWatch = System.Diagnostics.Stopwatch.StartNew();
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token);
            watchdog.CancelAfter(TimeSpan.FromMinutes(watchdogMinutes));
            try
            {
                await CloseGame(Hub.Config, watchdog.Token).ConfigureAwait(false);
                await StartGame(Hub.Config, watchdog.Token).ConfigureAwait(false);
                treeHuntLastRestartSeconds = restartWatch.Elapsed.TotalSeconds;
                return true;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                say($"[TreeHunt] Watchdog: the game restart took longer than {watchdogMinutes} min (try {attempt} of 3), so it was cancelled. Closing the game and trying again...");
                EchoUtil.Echo($"[Console {Connection.Name}] Watchdog: the game got stuck while restarting (try {attempt} of 3). Restarting it again.");
            }
        }
        return false;
    }

    private async Task PerformTreeHunt(PokedexSaveData dex, CancellationToken token)
    {
        var cfg = Settings.TreeHuntConditions;
        var dir = ConsoleReportFolder("TreeFinder");
        var file = System.IO.Path.Combine(dir, $"hunt_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        void Save(string line) => SafeAppendLine(file, line);
        void Say(string line) { Log(line); Save(line); }

        bool Wanted(TreeSavePrediction pr) => (!cfg.BurmyOnly || pr.Species == (ushort)Species.Burmy) && cfg.Target switch
        {
            TreeHuntTarget.ShinyAlpha => pr.Shiny && pr.Alpha,
            TreeHuntTarget.Shiny => pr.Shiny,
            TreeHuntTarget.Alpha => pr.Alpha,
            _ => pr.Shiny || pr.Alpha,
        };

        int rolls = cfg.ShinyRollsOverride > 0 ? cfg.ShinyRollsOverride : -1;
        var (perfect, complete) = CheckForPerfectComplete(HasCharm, dex, (ushort)Species.Burmy);
        int autoRolls = 1 + (complete ? 1 : 0) + (perfect ? 2 : 0) + (HasCharm ? 3 : 0) + (int)(SpawnType.Regular - 7);
        Say($"[TreeHunt] Report file: {file}");
        Say($"[TreeHunt] Started. Area: Crimson Mirelands. Target: {cfg.Target}. Shiny rolls: {(rolls > 0 ? $"{rolls} (override)" : $"{autoRolls} (automatic: shiny charm {HasCharm}, Burmy research perfect {perfect})")}. " +
            "Stand at the village entrance on a save, then let it run.");

        async Task Idle(string why)
        {
            Say($"[TreeHunt] {why}");
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        }

        List<(TreeSaveEntry Entry, TreeSavePrediction Pred, string Label)> BuildPreds(List<TreeSaveEntry> bpEntries)
        {
            var bpList = new List<(TreeSaveEntry Entry, TreeSavePrediction Pred, string Label)>();
            foreach (var bpEntry in bpEntries.Where(bq => bq.Tree.Burmy).GroupBy(bq => bq.Tree.Hash).Select(bg => bg.First()))
            {
                var bpMain = TreeSavePredictFull(dex, bpEntry.Tree.Table, bpEntry.Value, rolls);
                bpList.Add((bpEntry, bpMain, bpEntry.Tree.Id));
                if (bpMain.Ambiguous)
                    bpList.Add((bpEntry, TreeSavePredictFull(dex, bpEntry.Tree.Table, bpEntry.Value, rolls, 1), bpEntry.Tree.Id + "(alt)"));
            }
            return bpList;
        }

        List<(TreeSaveEntry Entry, TreeSavePrediction Pred, string Label)> PickHits(List<(TreeSaveEntry Entry, TreeSavePrediction Pred, string Label)> phAll) =>
            phAll.Where(px => Wanted(px.Pred) && (!cfg.OnlyShakingTrees || px.Pred.LikelyShaking)).ToList();

        string HitBlock(TreeSaveEntry hbEntry, TreeSavePrediction hbPred, string hbLabel)
        {
            var kind = hbPred.Shiny && hbPred.Alpha ? "SHINY ALPHA" : hbPred.Shiny ? "SHINY" : "ALPHA";
            var gender = hbPred.Gender == 0 ? "male" : hbPred.Gender == 1 ? "female" : "genderless";
            var t = hbEntry.Tree;
            var shake = hbPred.LikelyShaking ? "should be shaking" : "probably not shaking";
            return $"**{kind} {(Species)hbPred.Species}**, level {hbPred.Level}, {(Nature)hbPred.Nature} nature, {gender}\n" +
                   $"Tree {hbLabel}: {t.Place.Trim()} (X {t.X:F0}, Y {t.Y:F0}, Z {t.Z:F0}), {shake}\n" +
                   $"IVs (HP/Atk/Def/SpA/SpD/Spe): {string.Join("/", hbPred.IVs)}\n" +
                   $"EC: {hbPred.EC:X8} | PID: {hbPred.PID:X8} | Shake roll: {hbPred.ShakeScore} (needs under 10)";
        }

        void SendTreeMap()
        {
            try
            {
                var mapUrl = cfg.MapImageUrl?.Trim();
                if (!string.IsNullOrEmpty(mapUrl))
                {
                    var urlEmbed = new global::Discord.EmbedBuilder { Color = global::Discord.Color.Teal, Title = "Crimson Mirelands tree map", ImageUrl = mapUrl }.Build();
                    int sent = 0;
                    foreach (var fwd in EchoUtil.EmbedForwarders.ToArray())
                    {
                        try { fwd(string.Empty, urlEmbed); sent++; }
                        catch (Exception fx) when (fx is not OperationCanceledException) { Say($"[TreeHunt] A channel could not take the tree map: {fx.Message}"); }
                    }
                    Say(sent == 0 ? "[TreeHunt] Tree map not sent: no embed channel is set up (add one with the echo / embedHere commands)." : $"[TreeHunt] Sent the tree map link to {sent} channel(s).");
                    return;
                }

                var mapName = cfg.MapImageFile?.Trim();
                if (string.IsNullOrEmpty(mapName))
                {
                    Say("[TreeHunt] Tree map not sent: MapImageUrl and MapImageFile are both empty.");
                    return;
                }
                var mapPath = System.IO.Path.IsPathRooted(mapName) ? mapName : System.IO.Path.Combine(AppContext.BaseDirectory, "TreeFinder", mapName);
                if (!System.IO.File.Exists(mapPath))
                {
                    Say($"[TreeHunt] Tree map not sent: {mapPath} does not exist. Set MapImageUrl, or put the picture there.");
                    return;
                }
                var bytes = System.IO.File.ReadAllBytes(mapPath);
                var fileName = System.IO.Path.GetFileName(mapPath).Replace(' ', '_');
                var embed = new global::Discord.EmbedBuilder { Color = global::Discord.Color.Teal, Title = "Crimson Mirelands tree map", ImageUrl = $"attachment://{fileName}" };
                if (EchoUtil.RaidForwarders.Count == 0)
                    Say("[TreeHunt] Tree map not sent: no normal echo channel is set up (the picture file only goes to channels added with the echo command). Set MapImageUrl to reach every channel.");
                else
                    Say($"[TreeHunt] Sending the tree map ({fileName}, {bytes.Length / 1024} KB) to {EchoUtil.RaidForwarders.Count} echo channel(s).");
                EchoUtil.RaidEmbed(bytes, fileName, embed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Say($"[TreeHunt] Couldn't send the tree map ({ex.Message}); the match message was still sent.");
            }
        }

        void Announce(string anText, TreeSavePrediction anBest, bool withMap = true)
        {
            bool embedSent = false;
            try
            {
                var anImage = new PA8 { Species = anBest.Species, Gender = (byte)anBest.Gender };
                anImage.TID16 = 0;
                anImage.SID16 = 0;
                anImage.PID = anBest.Shiny ? 0u : 0x12345678u;
                if (EchoUtil.EmbedForwarders.Count > 0)
                {
                    EchoUtil.EchoEmbed(Hub.Config.StopConditions.MatchFoundEchoMention, anText, TradeExtensions<PK9>.PokeImg(anImage, false, false), "", true);
                    embedSent = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Say($"[TreeHunt] Couldn't build the embed picture ({ex.Message}); sending the plain message instead.");
            }
            if (!embedSent)
                EchoUtil.Echo(anText);
            if (withMap)
                SendTreeMap();
        }

        bool earlyStop = cfg.StopPressingWhenLoaded && cfg.Target != TreeHuntTarget.ListAll;

        int cycles = 0, failures = 0, totalAlpha = 0, totalShiny = 0, totalBoth = 0;
        var started = DateTime.Now;
        while (!token.IsCancellationRequested)
        {
            var cycleWatch = System.Diagnostics.Stopwatch.StartNew();
            var village = await TreeHuntReadTable(token).ConfigureAwait(false);
            var baseline = village == null ? null : TreeSaveValueMap(village.Value.Entries.Where(e => e.Tree.Burmy));
            Say($"[TreeHunt] Cycle {cycles + 1}: {(baseline == null ? "no table to compare with yet" : "baseline read")}. Walking back, then choosing the area...");

            await SetStick(LEFT, 0, -30_000, cfg.WalkBackMs, token).ConfigureAwait(false);
            await ResetStick(token).ConfigureAwait(false);

            for (int i = 0; i < cfg.OpenMenuPresses; i++)
                await Click(A, 1_000, token).ConfigureAwait(false);
            await Task.Delay(cfg.MenuOpenDelayMs, token).ConfigureAwait(false);
            int areaPresses = cfg.AreaSelectDpadPresses + (cfg.AreaSelectExtraPresses >= 0 ? cfg.AreaSelectExtraPresses : 1);
            for (int i = 0; i < areaPresses; i++)
                await Click(cfg.AreaSelectDirection == DpadDirection.Down ? DDOWN : DRIGHT, 0_700, token).ConfigureAwait(false);
            var walkMenuSeconds = cycleWatch.Elapsed.TotalSeconds;
            var (fresh, loadSeconds) = await TreeHuntPressAndWait(baseline, cfg.AreaSelectPresses, earlyStop, cfg.FieldLoadTimeoutSeconds, token).ConfigureAwait(false);
            if (fresh == null)
            {
                failures++;
                Say($"[TreeHunt] The new tree values did not appear within {cfg.FieldLoadTimeoutSeconds}s ({failures} in a row). Last table check: found {treeHuntLastFound} of {treeHuntLastExpected} trees.");
                if (failures >= 3)
                {
                    var msg = "Tree hunt stopped: the bot never reached the field. Check WalkBackMs, OpenMenuPresses, the D-pad direction and AreaSelectPresses.";
                    EchoUtil.Echo(msg);
                    await Idle(msg).ConfigureAwait(false);
                    return;
                }
                if (!await TreeHuntRestartGame(cfg.RestartWatchdogMinutes, Say, token).ConfigureAwait(false))
                {
                    var wmsg = $"[Console {Connection.Name}] Tree hunt stopped: the game would not restart after 3 tries (watchdog).";
                    EchoUtil.Echo(wmsg);
                    await Idle(wmsg).ConfigureAwait(false);
                    return;
                }
                continue;
            }

            failures = 0;
            cycles++;
            Say($"[TreeHunt] Table check: found {treeHuntLastFound} of {treeHuntLastExpected} trees in the game's table.");
            foreach (var d in TreeSaveDuplicateReport(fresh))
                Say($"[TreeHunt] DUPLICATE ENTRY: {d}");
            var preds = BuildPreds(fresh);
            int alphas = preds.Count(x => x.Pred.Alpha && !x.Pred.Alternative), shinies = preds.Count(x => x.Pred.Shiny && !x.Pred.Alternative), both = preds.Count(x => x.Pred.Alpha && x.Pred.Shiny && !x.Pred.Alternative);
            totalAlpha += alphas; totalShiny += shinies; totalBoth += both;
            Say($"[TreeHunt] Cycle {cycles}: {preds.Count(x => !x.Pred.Alternative)} trees scanned: {alphas} alpha, {shinies} shiny, {both} shiny alpha. (Totals: {totalAlpha} alpha, {totalShiny} shiny, {totalBoth} shiny alpha in {cycles} cycles, {(DateTime.Now - started).TotalMinutes:F0} min) Timing: walk+menu {walkMenuSeconds:F0}s, area load {loadSeconds:F0}s, last game restart {treeHuntLastRestartSeconds:F0}s.");
            if (cfg.OnlyShakingTrees && cfg.Target != TreeHuntTarget.ListAll)
                foreach (var sk in preds.Where(sx => !sx.Pred.Alternative && Wanted(sx.Pred) && !sx.Pred.LikelyShaking))
                    Say($"[TreeHunt] Skipped (not shaking, roll {sk.Pred.ShakeScore}): {(sk.Pred.Shiny && sk.Pred.Alpha ? "SHINY ALPHA" : sk.Pred.Shiny ? "SHINY" : "ALPHA")} tree {sk.Label} ({sk.Entry.Tree.Table}) {sk.Entry.Tree.Place} -- {TreeSaveDescribe(sk.Pred)}");

            if (cfg.Target == TreeHuntTarget.ListAll)
            {
                var ordered = preds.OrderBy(x => x.Label, StringComparer.Ordinal).ToList();
                var csvFile = System.IO.Path.Combine(dir, $"alltrees_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                var csv = new System.Text.StringBuilder();
                csv.AppendLine("TreeID,Table,Place,X,Y,Z,Species,Alpha,Shiny,Level,Nature,Gender,IV_HP,IV_Atk,IV_Def,IV_SpA,IV_SpD,IV_Spe,EC,PID,StoredValue,ShakeScore,LikelyShaking");
                foreach (var x in ordered)
                {
                    var t = x.Entry.Tree; var pr = x.Pred;
                    csv.AppendLine($"{x.Label},{t.Table},\"{t.Place}\",{t.X:F1},{t.Y:F1},{t.Z:F1},{pr.Name},{pr.Alpha},{pr.Shiny},{pr.Level},{(Nature)pr.Nature},{(pr.Gender == 0 ? "Male" : pr.Gender == 1 ? "Female" : "Genderless")},{string.Join(",", pr.IVs)},{pr.EC:X8},{pr.PID:X8},{x.Entry.Value:X16},{pr.ShakeScore},{pr.LikelyShaking}");
                }
                SafeWriteAllText(csvFile, csv.ToString());
                Say($"[TreeHunt] ListAll: wrote {ordered.Count} trees to {csvFile}");
                var chunk = new System.Text.StringBuilder("All Burmy trees for this visit (tree ID: Pokemon, level, gender, nature, IVs HP/Atk/Def/SpA/SpD/Spe, place, coordinates):\n");
                foreach (var x in ordered)
                {
                    var t = x.Entry.Tree; var pr = x.Pred;
                    var line = $"{(pr.LikelyShaking ? "[SHAKING?] " : "")}{x.Label}: {(pr.Shiny ? "SHINY " : "")}{(pr.Alpha ? "ALPHA " : "")}{pr.Name} Lv{pr.Level} {(pr.Gender == 0 ? "M" : pr.Gender == 1 ? "F" : "-")} {(Nature)pr.Nature} IVs {string.Join("/", pr.IVs)} | {t.Place} ({t.X:F0}, {t.Y:F0}, {t.Z:F0})\n";
                    if (chunk.Length + line.Length > 1_800) { EchoUtil.Echo(chunk.ToString()); chunk.Clear(); }
                    chunk.Append(line);
                    Save(line.TrimEnd());
                }
                chunk.Append("Game is paused at the Home menu. Type $continue to scan a fresh visit.");
                EchoUtil.Echo(chunk.ToString());
                await Click(HOME, 1_000, token).ConfigureAwait(false);
                IsWaiting = true;
                while (IsWaiting)
                    await Task.Delay(1_000, token).ConfigureAwait(false);
                try
                {
                    var again = await TreeHuntReadTable(token).ConfigureAwait(false);
                    if (again != null)
                    {
                        var before = TreeSaveValueMap(fresh.Where(e => e.Tree.Burmy));
                        var after = again.Value.Entries.Where(e => e.Tree.Burmy).GroupBy(e => e.Tree.Hash).Select(g => g.First()).ToList();
                        int changed = after.Count(e => !before.TryGetValue(e.Tree.Hash, out var old) || old != e.Value);
                        Say($"[TreeHunt] Re-check: {changed} of {after.Count} tree values are different from the scan.");
                        EchoUtil.Echo($"Re-check: {changed} of {after.Count} tree values changed since the scan." + (changed > 0 ? " A new list of the CURRENT values has been saved as a CSV in the TreeFinder folder." : " The list you were given was still correct."));
                        if (changed > 0)
                        {
                            var csv2 = new System.Text.StringBuilder();
                            csv2.AppendLine("TreeID,Table,Place,X,Y,Z,Species,Alpha,Shiny,Level,Nature,Gender,IV_HP,IV_Atk,IV_Def,IV_SpA,IV_SpD,IV_Spe,EC,PID,StoredValue,ChangedSinceScan,ShakeScore,LikelyShaking");
                            foreach (var e in after.OrderBy(e => e.Tree.Id, StringComparer.Ordinal))
                            {
                                var t = e.Tree; var pr = TreeSavePredictFull(dex, t.Table, e.Value, rolls);
                                bool ch = !before.TryGetValue(t.Hash, out var old2) || old2 != e.Value;
                                csv2.AppendLine($"{t.Id},{t.Table},\"{t.Place}\",{t.X:F1},{t.Y:F1},{t.Z:F1},{pr.Name},{pr.Alpha},{pr.Shiny},{pr.Level},{(Nature)pr.Nature},{(pr.Gender == 0 ? "Male" : pr.Gender == 1 ? "Female" : "Genderless")},{string.Join(",", pr.IVs)},{pr.EC:X8},{pr.PID:X8},{e.Value:X16},{ch},{pr.ShakeScore},{pr.LikelyShaking}");
                            }
                            SafeWriteAllText(System.IO.Path.Combine(dir, $"alltrees_after_{DateTime.Now:yyyyMMdd_HHmmss}.csv"), csv2.ToString());
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Say($"[TreeHunt] Re-check failed ({ex.Message}).");
                }
                Say("[TreeHunt] Resuming.");
                if (!await TreeHuntRestartGame(cfg.RestartWatchdogMinutes, Say, token).ConfigureAwait(false))
                {
                    var wmsg = $"[Console {Connection.Name}] Tree hunt stopped: the game would not restart after 3 tries (watchdog).";
                    EchoUtil.Echo(wmsg);
                    await Idle(wmsg).ConfigureAwait(false);
                    return;
                }
                continue;
            }

            if (earlyStop && cfg.MatchSettleSeconds > 0 && (cycles == 1 || cycles % 100 == 0 || PickHits(preds).Count > 0))
            {
                for (int si = 0; si < cfg.MatchSettleSeconds; si++)
                    await Click(A, 1_000, token).ConfigureAwait(false);
                var settled = await TreeHuntReadTable(token).ConfigureAwait(false);
                if (settled != null)
                {
                    var oldMap = TreeSaveValueMap(fresh.Where(fx => fx.Tree.Burmy));
                    var newMap = TreeSaveValueMap(settled.Value.Entries.Where(fx => fx.Tree.Burmy));
                    int settleDiff = newMap.Count(kv => !oldMap.TryGetValue(kv.Key, out var oldVal) || oldVal != kv.Value);
                    if (settleDiff > 0)
                    {
                        earlyStop = false;
                        Say($"[TreeHunt] WARNING: {settleDiff} tree values changed while the area finished loading, so reading early is not reliable. Switching back to pressing A the full {cfg.AreaSelectPresses} times for the rest of this run.");
                        EchoUtil.Echo($"[Console {Connection.Name}] Tree hunt: values changed after loading, so the fast mode was switched off for this run.");
                        fresh = settled.Value.Entries;
                        preds = BuildPreds(fresh);
                    }
                    else
                        Say($"[TreeHunt] Settle check passed: the values did not change after loading.");
                }
            }

            var hits = PickHits(preds);
            if (hits.Count > 0)
            {
                var lines = hits.Select(h => HitBlock(h.Entry, h.Pred, h.Label)).ToList();
                var elapsed = DateTime.Now - started;
                var took = elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m" : $"{elapsed.Minutes}m";
                var text = $"**Tree hunt: match found** ({Connection.Name})\nAfter {cycles:N0} cycles, {took}.\n\n" + string.Join("\n\n", lines) +
                    $"\n\nThe game is paused at the Home menu. Go to the tree above (it has to be shaking). Type `$continue {Connection.Name}` to resume the hunt.";
                foreach (var line in lines)
                    Say($"[TreeHunt] FOUND: {line.Replace("\n", " | ")}");
                var bestHit = hits.OrderByDescending(h => (h.Pred.Shiny ? 2 : 0) + (h.Pred.Alpha ? 1 : 0)).First();
                Announce(text, bestHit.Pred);
                await Click(HOME, 1_000, token).ConfigureAwait(false);
                IsWaiting = true;
                var pausedAt = DateTime.Now;
                var lastReminder = pausedAt;
                while (IsWaiting)
                {
                    await Task.Delay(1_000, token).ConfigureAwait(false);
                    if (cfg.MatchReminderMinutes > 0 && (DateTime.Now - lastReminder).TotalMinutes >= cfg.MatchReminderMinutes)
                    {
                        lastReminder = DateTime.Now;
                        Announce($"**Reminder** ({Connection.Name}): the hunt has been paused at a match since {pausedAt:HH:mm}. Type `$continue {Connection.Name}` when you are done.\n\n" + string.Join("\n\n", lines), bestHit.Pred, false);
                    }
                }
                try
                {
                    var again2 = await TreeHuntReadTable(token).ConfigureAwait(false);
                    if (again2 == null)
                    {
                        Say("[TreeHunt] Re-check: could not read the tree table (the game may not be on the field).");
                        EchoUtil.Echo($"[Console {Connection.Name}] Re-check: the tree table could not be read, so I can't say whether the values changed.");
                    }
                    else
                    {
                        var before2 = TreeSaveValueMap(fresh.Where(rv => rv.Tree.Burmy));
                        var after2 = again2.Value.Entries.Where(rv => rv.Tree.Burmy).GroupBy(rv => rv.Tree.Hash).Select(g => g.First()).ToList();
                        int changed2 = after2.Count(rv => !before2.TryGetValue(rv.Tree.Hash, out var ro) || ro != rv.Value);
                        Say($"[TreeHunt] Re-check: {changed2} of {after2.Count} tree values are different from the scan.");
                        EchoUtil.Echo($"[Console {Connection.Name}] Re-check: {changed2} of {after2.Count} tree values changed since the scan." + (changed2 > 0 ? " A list of the CURRENT values has been saved as a CSV in the TreeFinder folder." : " The list you were given was still correct."));
                        if (changed2 > 0)
                        {
                            var csv3 = new System.Text.StringBuilder();
                            csv3.AppendLine("TreeID,Table,Place,X,Y,Z,Species,Alpha,Shiny,Level,Nature,Gender,IV_HP,IV_Atk,IV_Def,IV_SpA,IV_SpD,IV_Spe,EC,PID,StoredValue,ChangedSinceScan,ShakeScore,LikelyShaking");
                            foreach (var rv in after2.OrderBy(rv => rv.Tree.Id, StringComparer.Ordinal))
                            {
                                var rt = rv.Tree; var rp = TreeSavePredictFull(dex, rt.Table, rv.Value, rolls);
                                bool rch = !before2.TryGetValue(rt.Hash, out var ro2) || ro2 != rv.Value;
                                csv3.AppendLine($"{rt.Id},{rt.Table},\"{rt.Place}\",{rt.X:F1},{rt.Y:F1},{rt.Z:F1},{rp.Name},{rp.Alpha},{rp.Shiny},{rp.Level},{(Nature)rp.Nature},{(rp.Gender == 0 ? "Male" : rp.Gender == 1 ? "Female" : "Genderless")},{string.Join(",", rp.IVs)},{rp.EC:X8},{rp.PID:X8},{rv.Value:X16},{rch},{rp.ShakeScore},{rp.LikelyShaking}");
                            }
                            SafeWriteAllText(System.IO.Path.Combine(dir, $"alltrees_after_{DateTime.Now:yyyyMMdd_HHmmss}.csv"), csv3.ToString());
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Say($"[TreeHunt] Re-check failed ({ex.Message}).");
                }
                Say("[TreeHunt] Resuming the hunt.");
            }
            else if (cfg.ProgressEveryCycles > 0 && cycles % cfg.ProgressEveryCycles == 0)
                EchoUtil.Echo($"[Console {Connection.Name}] Tree hunt: {cycles} cycles, {cycles * (treeHuntLastExpected > 0 ? treeHuntLastExpected : 56)} trees scanned, {totalAlpha} alpha, {totalShiny} shiny, {totalBoth} shiny alpha so far ({(DateTime.Now - started).TotalHours:F1} h). Target: {cfg.Target}.");

            if (!await TreeHuntRestartGame(cfg.RestartWatchdogMinutes, Say, token).ConfigureAwait(false))
            {
                var wmsg = $"[Console {Connection.Name}] Tree hunt stopped: the game would not restart after 3 tries (watchdog).";
                EchoUtil.Echo(wmsg);
                await Idle(wmsg).ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task PerformMultiBunnyScan(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            int count = 2;
            ulong key = (ulong)(0x1331B1B112345678 + Util.Rand.Next(1, 999999999));
            var slots = new SlotDetail[]
            {
                new(25, "Psyduck", false, [13, 16], 0),
                new(2, "Psyduck", true , [28, 31], 3),
                new(25, "Buneary", false, [13, 16], 0),
                new(2, "Buneary", true , [28, 31], 3),
            };
            SetFakeTable(slots, key);

            string log = string.Empty;
            var groupID = (int)Settings.MultiScanConditions.MultiSpecies;
            var ofs = new long[] { 0x42A6EE0, 0x330, 0x70 + groupID * 0x440 + 0x20 };
            var multiptr = await SwitchConnection.PointerAll(ofs, token).ConfigureAwait(false);
            var GeneratorSeed = BitConverter.ToUInt64(await SwitchConnection.ReadBytesAbsoluteAsync(multiptr, 8, token).ConfigureAwait(false), 0);
            var group_seed = (GeneratorSeed - 0x82A2B175229D6A5B) & 0xFFFFFFFFFFFFFFFF;
            //Log($"Seed: {group_seed:X16}");

            var details = new SpawnCount(count, count);
            var set = new SpawnSet(key, count);
            var spawner = SpawnInfo.GetLoop(details, set, SpawnType.Regular);

            var results = Permuter.Permute(spawner, group_seed, Settings.MultiScanConditions.Advances);
            if (!results.HasResults)
                log += $"\nNo results found within {Settings.MultiScanConditions.Advances} advances :(";
            else
            {
                var lines = results.GetLines();
                foreach (var line in lines)
                    log += "\n" + line;
            }
            Log($"{log}");
            IsWaiting = true;
            while (IsWaiting)
                await Task.Delay(1_000, token).ConfigureAwait(false);
        }
    }

    private async Task PerformMultiBasculinScan(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            int count = 2;
            ulong key = (ulong)(0x1337B0BACAFEB00B + Util.Rand.Next(1, 999999999));
            var slots = new SlotDetail[]
            {
                new(100, "Basculin-2", false, [41, 44], 0),
                new(2, "Basculin-2", true , [56, 59], 3),
            };
            SetFakeTable(slots, key);

            string log = string.Empty;
            var groupID = (int)Settings.MultiScanConditions.MultiSpecies;
            var ofs = new long[] { 0x42A6EE0, 0x330, 0x70 + groupID * 0x440 + 0x20 };
            var multiptr = await SwitchConnection.PointerAll(ofs, token).ConfigureAwait(false);
            var GeneratorSeed = BitConverter.ToUInt64(await SwitchConnection.ReadBytesAbsoluteAsync(multiptr, 8, token).ConfigureAwait(false), 0);
            var group_seed = (GeneratorSeed - 0x82A2B175229D6A5B) & 0xFFFFFFFFFFFFFFFF;
            //Log($"Seed: {group_seed:X16}");

            var details = new SpawnCount(count, count);
            var set = new SpawnSet(key, count);
            var spawner = SpawnInfo.GetLoop(details, set, SpawnType.Regular);

            var results = Permuter.Permute(spawner, group_seed, Settings.MultiScanConditions.Advances);
            if (!results.HasResults)
                log += $"\nNo results found within {Settings.MultiScanConditions.Advances} advances :(";
            else
            {
                var lines = results.GetLines();
                foreach (var line in lines)
                    log += "\n" + line;
            }
            Log($"{log}");
            IsWaiting = true;
            while (IsWaiting)
                await Task.Delay(1_000, token).ConfigureAwait(false);
        }
    }

    private async Task PerformMultiHippoScan(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            int count = 3;
            ulong key = (ulong)(0x1221B3A312345678 + Util.Rand.Next(1, 999999999));
            var slots = new SlotDetail[]
            {
                new(30, "Hippopotas", false, [30, 33], 0),
                new(2, "Hippopotas", true , [45, 48], 3),
                new(100, "Hippowdon", false, [43, 46], 0),
                new(1, "Hippowdon", true , [58, 61], 3),
            };
            SetFakeTable(slots, key);

            string log = string.Empty;
            var groupID = (int)Settings.MultiScanConditions.MultiSpecies;
            var ofs = new long[] { 0x42A6EE0, 0x330, 0x70 + groupID * 0x440 + 0x20 };
            var multiptr = await SwitchConnection.PointerAll(ofs, token).ConfigureAwait(false);
            var GeneratorSeed = BitConverter.ToUInt64(await SwitchConnection.ReadBytesAbsoluteAsync(multiptr, 8, token).ConfigureAwait(false), 0);
            var group_seed = (GeneratorSeed - 0x82A2B175229D6A5B) & 0xFFFFFFFFFFFFFFFF;
            //Log($"Seed: {group_seed:X16}");

            var details = new SpawnCount(count, count);
            var set = new SpawnSet(key, count);
            var spawner = SpawnInfo.GetLoop(details, set, SpawnType.Regular);

            var results = Permuter.Permute(spawner, group_seed, Settings.MultiScanConditions.Advances);
            if (!results.HasResults)
                log += $"\nNo results found within {Settings.MultiScanConditions.Advances} advances :(";
            else
            {
                var lines = results.GetLines();
                foreach (var line in lines)
                    log += "\n" + line;
            }
            Log($"{log}");
            IsWaiting = true;
            while (IsWaiting)
                await Task.Delay(1_000, token).ConfigureAwait(false);
        }
    }

    private async Task PerformMultiMagikarpScan(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            int count = 2;
            ulong key = (ulong)(0x1221B1B112345678 + Util.Rand.Next(1, 999999999));
            var slots = new SlotDetail[]
            {
                new(100, "Magikarp", false, [16, 19], 0),
                new(2, "Magikarp", true , [31, 34], 3),
                new(30, "Gyarados", false, [53, 56], 0),
                new(1, "Gyarados", true , [68, 71], 3),
            };
            SetFakeTable(slots, key);

            string log = string.Empty;
            var groupID = (int)Settings.MultiScanConditions.MultiSpecies;
            var ofs = new long[] { 0x42A6EE0, 0x330, 0x70 + groupID * 0x440 + 0x20 };
            var multiptr = await SwitchConnection.PointerAll(ofs, token).ConfigureAwait(false);
            var GeneratorSeed = BitConverter.ToUInt64(await SwitchConnection.ReadBytesAbsoluteAsync(multiptr, 8, token).ConfigureAwait(false), 0);
            var group_seed = (GeneratorSeed - 0x82A2B175229D6A5B) & 0xFFFFFFFFFFFFFFFF;
            //Log($"Seed: {group_seed:X16}");

            var details = new SpawnCount(count, count);
            var set = new SpawnSet(key, count);
            var spawner = SpawnInfo.GetLoop(details, set, SpawnType.Regular);

            var results = Permuter.Permute(spawner, group_seed, Settings.MultiScanConditions.Advances);
            if (!results.HasResults)
                log += $"\nNo results found within {Settings.MultiScanConditions.Advances} advances :(";
            else
            {
                var lines = results.GetLines();
                foreach (var line in lines)
                    log += "\n" + line;
            }
            Log($"{log}");
            IsWaiting = true;
            while (IsWaiting)
                await Task.Delay(1_000, token).ConfigureAwait(false);
        }
    }

    private async Task PerformMMOScan(PokedexSaveData dex, CancellationToken token)
    {
        List<string> logs = [];
        List<string> mmoactive = [];
        var ofs = new long[] { 0x42BA6B0, 0x2B0, 0x58, 0x18, 0x1B0 };
        var outbreakptr = await SwitchConnection.PointerAll(ofs, token).ConfigureAwait(false);
        var info = await SwitchConnection.ReadBytesAbsoluteAsync(outbreakptr, 14720, token).ConfigureAwait(false);

        for (int mapcount = 0; mapcount < 5; mapcount++)
        {
            Log($"Checking map #{mapcount + 1}...");
            ofs = [0x42BA6B0, 0x2B0, 0x58, 0x18, 0x1B0 + (mapcount * 0xB80)];
            outbreakptr = await SwitchConnection.PointerAll(ofs, token).ConfigureAwait(false);
            var active = BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(outbreakptr, 2, token).ConfigureAwait(false), 0);
            bool areavalid = $"{active:X4}" is not ("0000" or "2645");
            if (!areavalid)
                continue;

            var mapinfo = info.AsSpan(mapcount * 2944, 2944).ToArray();

            string map = $"{active:X4}" switch
            {
                "56B7" => " in the Cobalt Coastlands",
                "5504" => " in the Crimson Mirelands",
                "5351" => " in the Alabaster Icelands",
                "519E" => " in the Coronet Highlands",
                "5A1D" => " in the Obsidian Fieldlands",
                _ => throw new NotImplementedException("Unknown map."),
            };

            var groupcount = 0;
            var species = (Species)BitConverter.ToUInt16(mapinfo.AsSpan(36 + (groupcount * 144), 2).ToArray(), 0);
            while (species != Species.None && species < Species.MAX_COUNT)
            {
                var spawncount = BitConverter.ToUInt16(mapinfo.AsSpan(112 + (groupcount * 144), 2).ToArray(), 0);
                var encslot = BitConverter.ToUInt64(mapinfo.AsSpan(72 + (groupcount * 144), 8).ToArray(), 0);
                var bonusencslot = BitConverter.ToUInt64(mapinfo.AsSpan(80 + (groupcount * 144), 8).ToArray(), 0);
                var bonuscount = BitConverter.ToUInt16(mapinfo.AsSpan(132 + (groupcount * 144), 2).ToArray(), 0);
                var group_seed = BitConverter.ToUInt64(mapinfo.AsSpan(104 + (groupcount * 144), 8).ToArray(), 0);
                var xyz = mapinfo.AsSpan(16 + (groupcount * 144), 12).ToArray();
                var spawncoordx = BitConverter.ToUInt32(xyz, 0);
                var spawncoordy = BitConverter.ToUInt32(xyz, 4);
                var spawncoordz = BitConverter.ToUInt32(xyz, 8);
                mmoactive.Add($"\nGroup Seed: {string.Format("0x{0:X}", group_seed)}\nMassive Mass Outbreak found for: {species}{map} | Total Spawn Count: {spawncount} | Group ID: {groupcount}");
                List<PA8> pokelist;
                List<PA8> bonuslist;
                (pokelist, bonuslist) = ReadMMOSeed(dex, spawncount, group_seed, bonuscount, encslot, bonusencslot);
                for (int p = 0; p < pokelist.Count; p++)
                {
                    var pk = pokelist[p];
                    var print = Hub.Config.StopConditions.GetAlphaPrintName(pk);
                    var msg = $"\nRegular Spawn: #{p + 1}" + print;
                    logs.Add(msg);
                    if (pk.IsShiny)
                    {
                        Log($"{msg}\n\nAutofilling coords for {(Species)pk.Species}. Check the Results tab for more information.");
                        coordinates = ($"{spawncoordx:X8}", $"{spawncoordy:X8}", $"{spawncoordz:X8}");
                        await CheckEmbed(pk, map, msg, token).ConfigureAwait(false);
                    }
                }

                for (int b = 0; b < bonuslist.Count; b++)
                {
                    var pk = bonuslist[b];
                    var print = Hub.Config.StopConditions.GetAlphaPrintName(pk);
                    var msg = $"\nBonus Spawn: #{b + 1}" + print;
                    logs.Add(msg);
                    if (pk.IsShiny)
                    {
                        Log($"{msg}\n\nAutofilling coords for {(Species)pk.Species}. Check the Results tab for more information.");
                        coordinates = ($"{spawncoordx:X8}", $"{spawncoordy:X8}", $"{spawncoordz:X8}");
                        await CheckEmbed(pk, map, msg, token).ConfigureAwait(false);
                    }
                }
                var report = string.Join("\n", logs);
                Log("[MMOScan]\n"+report);
                groupcount++;
                species = (Species)BitConverter.ToUInt16(mapinfo.AsSpan(36 + (groupcount * 144), 2).ToArray(), 0);
            }
            var actives = string.Join("", mmoactive);
            Log(actives);
        }
    }

    private async Task MassiveOutbreakHunter(PokedexSaveData dex, CancellationToken token)
    {
        if (Settings.OutbreakConditions.CheckBoxes)
        {
            Log("Initiating box reader...");
            boxlist = await ReadPokemonBoxes(token).ConfigureAwait(false);
        }
        int attempts = 0;
        while (!token.IsCancellationRequested)
        {
            if (Settings.OutbreakConditions.CheckDistortionFirst)
            {
                Log("Checking our distortions before scanning outbreaks!");
                await DistortionReader(dex, token).ConfigureAwait(false);
            }
            Log($"Search #{attempts + 1}: Reading map for active outbreaks...");
            await Click(Y, 1_000, token).ConfigureAwait(false);
            while (!await IsOnOverworldTitle(token).ConfigureAwait(false))
                await Click(A, 0_800, token).ConfigureAwait(false);

            var type = Settings.OutbreakConditions.TypeOfScan;
            switch (type)
            {
                case OutbreakScanType.Both: await PerformOutbreakScan(dex, token).ConfigureAwait(false); await PerformMMOScan(dex, token).ConfigureAwait(false); break;
                case OutbreakScanType.OutbreakOnly: await PerformOutbreakScan(dex, token).ConfigureAwait(false); break;
                case OutbreakScanType.MMOOnly: await PerformMMOScan(dex, token).ConfigureAwait(false); break;
            }

            Log("No match found, resetting.");
            await Click(B, 1_000, token).ConfigureAwait(false);
            await CloseGame(Hub.Config, token).ConfigureAwait(false);
            await StartGame(Hub.Config, token).ConfigureAwait(false);
            attempts++;
        }
    }

    public async Task<List<PA8>> ReadPokemonBoxes(CancellationToken token)
    {
        var b1s1 = new long[] { 0x42BA6B0, 0x1F0, 0x68 };
        var boxStart = await SwitchConnection.PointerAll(b1s1, token).ConfigureAwait(false);
        var data = await SwitchConnection.ReadBytesAbsoluteAsync(boxStart, BoxFormatSlotSize * 960, token).ConfigureAwait(false);

        for (int slot = 0; slot < 960; slot++)
        {
            int slotAdjustment = slot * BoxFormatSlotSize;
            byte[] dataSlice = data.AsSpan(slotAdjustment, BoxFormatSlotSize).ToArray();
            boxlist.Add(new PA8(dataSlice));
        }
        return boxlist;
    }
}