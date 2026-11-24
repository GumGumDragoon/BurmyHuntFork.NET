using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using SysBot.Base;

namespace SysBot.Pokemon;

public class ArceusBotSettings : IBotStateSettings, ICountSettings
{
    private const string Arceus = nameof(Arceus);
    private const string Counts = nameof(Counts);
    public override string ToString() => "Arceus Bot Settings";

    [Category(Arceus), Description("When enabled, the bot will continue after finding a suitable match.")]
    public ContinueAfterMatch ContinueAfterMatch { get; set; } = ContinueAfterMatch.StopExit;

    [Category(Arceus), Description("The method by which the bot will scan overworld Pokémon.")]
    public ArceusMode BotType { get; set; } = ArceusMode.PlayerCoordScan;

    [Category(Arceus), Description("Will hunt for the desired outbreak species if not empty. Separate species with a comma. Ex: Eevee,Rotom,Voltorb")]
    public string SpeciesToHunt { get; set; } = string.Empty;

    [Category(Arceus), Description("Special Conditions"), DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
    public SpecialFiltersCategory SpecialConditions { get; set; } = new();

    [Category(Arceus), Description("Outbreak Conditions"), DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
    public OutbreakFiltersCategory OutbreakConditions { get; set; } = new();

    [Category(Arceus), Description("Distortion Conditions"), DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
    public DistortionFiltersCategory DistortionConditions { get; set; } = new();

    [Category(Arceus), Description("MultiScan Conditions"), DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
    public MultiScanFiltersCategory MultiScanConditions { get; set; } = new();

    [Category(Arceus), Description("AlphaScan Conditions"), DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
    public AlphaScanFiltersCategory AlphaScanConditions { get; set; } = new();

    [Category(Arceus), Description("Tree Hunt Conditions"), DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
    public TreeHuntFiltersCategory TreeHuntConditions { get; set; } = new();

    [Category(Arceus), Description("When enabled, the screen will be turned off during normal bot loop operation to save power.")]
    public bool ScreenOff { get; set; }

    [Category(Arceus), Description("Enter Discord channel ID(s) to post Arceus embeds to. Feature has to be initialized via \"$arceusEmbed\" after every client restart.")]
    public string ArceusEmbedChannels { get; set; } = string.Empty;

    [Category(Arceus), TypeConverter(typeof(CategoryConverter<OutbreakFiltersCategory>))]
    public class OutbreakFiltersCategory
    {
        public override string ToString() => "Outbreak Conditions";

        [Category(Arceus), Description("Select the type of scan to perform for the Outbreak/MMO Hunter.")]
        public OutbreakScanType TypeOfScan { get; set; } = OutbreakScanType.Both;

        [Category(Arceus), Description("When enabled, the bot will teleport instead of walk/run for Outbreak/MMO Hunter.")]
        public bool TeleportToHunt { get; set; } = false;

        [Category(Arceus), Description("When enabled, the bot will search for only an alpha shiny from a MMO.")]
        public bool AlphaShinyOnly { get; set; } = false;

        [Category(Arceus), Description("When enabled, the bot will check all Distortion spawns before going into Jubilife to read spawns.")]
        public bool CheckDistortionFirst { get; set; } = false;

        [Category(Arceus), Description("EXPERIMENTAL, does not work 100% of the time. When enabled, the bot will read every box to see if the encounter exists. It checks for Species, Shiny, and IsAlpha.")]
        public bool CheckBoxes { get; set; } = false;

        [Category(Arceus), Description("Toggle true if you just want to stop the bot when a desired species is found in a MMO or MO.")]
        public bool StopIfSpeciesFound { get; set; } = true;
    }

    [Category(Arceus), TypeConverter(typeof(CategoryConverter<DistortionFiltersCategory>))]
    public class DistortionFiltersCategory
    {
        public override string ToString() => "Distortion Conditions";

        [Category(Arceus), Description("Select the Location of the map you are hunting distortions for.")]
        public ArceusMap DistortionLocation { get; set; } = ArceusMap.ObsidianFieldlands;

        [Category(Arceus), Description("Wait time in minutes before starting a new distortion. If one does not spawn initially, stop and start the bot again.")]
        public int WaitTimeDistortion { get; set; } = 1;

        [Category(Arceus), Description("When enabled, the bot will only stop on Alpha Shinies in Distortions.")]
        public bool ShinyAlphaOnly { get; set; } = false;

        [Category(Arceus), Description("When enabled, the bot will stop when an Alpha is found in Distortions.")]
        public bool AnyAlpha { get; set; } = false;

        [Category(Arceus), Description("When enabled, the bot will teleport to the location of the match before pressing HOME.")]
        public bool TeleportToDistortionLocation { get; set; } = false;

        [Category(Arceus), Description("Only stop on matches of this gender. Set to Any to disable this filter.")]
        public GenderType DesiredGender { get; set; } = GenderType.Any;
    }

    [Category(Arceus), TypeConverter(typeof(CategoryConverter<MultiScanFiltersCategory>))]
    public class MultiScanFiltersCategory
    {
        public override string ToString() => "MultiScan Conditions";

        [Category(Arceus), Description("Enter the max number of advances for pathing.")]
        public int Advances { get; set; } = 20;

        [Category(Arceus), Description("Species for MultiSpawners Group ID")]
        public MultiSpawners MultiSpecies { get; set; } = MultiSpawners.Eevee;
    }

    [Category(Arceus), TypeConverter(typeof(CategoryConverter<TreeHuntFiltersCategory>))]
    public class TreeHuntFiltersCategory
    {
        public override string ToString() => "Tree Hunt Conditions";

        [Category(Arceus), Description("What the tree hunt stops for. ShinyAlpha: only a shiny alpha. Shiny / Alpha: either one alone. ShinyOrAlpha: any shiny or any alpha.")]
        public TreeHuntTarget Target { get; set; } = TreeHuntTarget.ShinyAlpha;

        [Category(Arceus), Description("Extra presses of the area D-pad button on top of AreaSelectDpadPresses to reach the Crimson Mirelands (one step past Obsidian Fieldlands). -1 = automatic (1). Set an exact number if the automatic one lands on the wrong area.")]
        public int AreaSelectExtraPresses { get; set; } = -1;

        [Category(Arceus), Description("Only stop for a Burmy. When on, a shiny or alpha Wormadam or Pachirisu from the same tree is ignored.")]
        public bool BurmyOnly { get; set; } = true;

        [Category(Arceus), Description("Milliseconds to hold the stick backwards after the save loads, to reach the village entrance.")]
        public int WalkBackMs { get; set; } = 3000;

        [Category(Arceus), Description("Step 1 at the village entrance: how many times to press A to open the area menu. Set to 0 if the menu opens by itself when you reach the entrance.")]
        public int OpenMenuPresses { get; set; } = 1;

        [Category(Arceus), Description("Milliseconds to wait for the area menu to appear before moving the cursor.")]
        public int MenuOpenDelayMs { get; set; } = 2000;

        [Category(Arceus), Description("Step 2: which D-pad button moves the menu cursor from Jubilife Village to Obsidian Fieldlands.")]
        public DpadDirection AreaSelectDirection { get; set; } = DpadDirection.Right;

        [Category(Arceus), Description("How many times to press that D-pad button to land on Obsidian Fieldlands.")]
        public int AreaSelectDpadPresses { get; set; } = 1;

        [Category(Arceus), Description("Step 3: how many times to press A (one per second) AFTER the cursor is on Obsidian Fieldlands, to confirm and get through the loading screen.")]
        public int AreaSelectPresses { get; set; } = 12;

        [Category(Arceus), Description("Seconds to wait for the new tree values to appear after choosing the area. After 3 failures in a row the hunt stops so you can check the movement.")]
        public int FieldLoadTimeoutSeconds { get; set; } = 60;

        [Category(Arceus), Description("Watchdog for the game restart between cycles. If closing and restarting the game takes longer than this many minutes (for example a loading screen that never ends), it is cancelled and tried again, up to 3 times, then the hunt stops. 0 = off. A normal restart takes under a minute.")]
        public int RestartWatchdogMinutes { get; set; } = 4;

        [Category(Arceus), Description("Faster cycles: stop pressing A as soon as the new tree values have appeared and stayed the same, instead of always pressing A the full number of times. The bot checks itself (on the first cycle, every 100th cycle and at every match) that the early values are the same as the settled ones, and switches this off for the run if they are not. Turn it off to always press the full number of times.")]
        public bool StopPressingWhenLoaded { get; set; } = true;

        [Category(Arceus), Description("Seconds the bot waits for the area to finish loading before it double-checks the tree values (used by the fast mode above on a match, the first cycle and every 100th cycle). 0 = never double-check.")]
        public int MatchSettleSeconds { get; set; } = 15;

        [Category(Arceus), Description("While the hunt is paused for a match, post a reminder (with the mention) to Discord every this many minutes, until you type $continue. 0 = no reminders.")]
        public int MatchReminderMinutes { get; set; } = 30;

        [Category(Arceus), Description("Web link to the numbered tree map picture (a direct link to the image, e.g. a raw.githubusercontent.com address). When set, the map is sent in an embed to every echo channel right after a match. Empty = use MapImageFile instead.")]
        public string MapImageUrl { get; set; } = "https://raw.githubusercontent.com/GumGumDragoon/BurmyHuntTrees/refs/heads/main/crimson_tree_numbers.jpg";

        [Category(Arceus), Description("Picture of the numbered tree map, sent to Discord right after a match is announced so you can find the tree. A bare file name is looked for in the TreeFinder folder next to the bot program (a full path also works). Only used when MapImageUrl is empty.")]
        public string MapImageFile { get; set; } = "crimson_tree_numbers.jpg";

        [Category(Arceus), Description("Send a Discord progress message every this many cycles (0 = never).")]
        public int ProgressEveryCycles { get; set; } = 50;

        [Category(Arceus), Description("0 = work out the shiny rolls automatically (charm and Pokedex research). If the log's assumed number is wrong for your save, set the real number here (it is 7 with the shiny charm and perfect research).")]
        public int ShinyRollsOverride { get; set; } = 0;

        [Category(Arceus), Description("When enabled, only trees that look like they are shaking (their shake roll is below 10, usually about 4 to 6 trees per visit) can trigger a match. Disable it to match on every tree.")]
        public bool OnlyShakingTrees { get; set; } = true;

        [Category(Arceus), Description("ON (default): trees whose entry roll is the alpha slot are predicted as alpha Burmy. Turn it OFF to treat every tree as a normal Burmy (for example if alpha trees keep giving normal ones).")]
        public bool AlphaSlotsSpawnAlpha { get; set; } = true;
    }

    [Category(Arceus), TypeConverter(typeof(CategoryConverter<AlphaScanFiltersCategory>))]
    public class AlphaScanFiltersCategory
    {
        public override string ToString() => "AlphaScan Conditions";

        [Category(Arceus), Description("Select the Location to Autofill Coords upon running PlayerCoordScan.")]
        public ArceusAutoFill AutoFillCoords { get; set; } = ArceusAutoFill.CampZone;

        [Category(Arceus), Description("Enter number of shiny rolls for Static Alphas.")]
        public ShinyRolls StaticAlphaShinyRolls { get; set; } = ShinyRolls.PerfectCharm;            

        [Category(Arceus), Description("Enter number of advances to do.")]
        public int Advances { get; set; } = 1;

        [Category(Arceus), Description("Toggle true if you just entered the map and didn't spawn the Pokémon")]
        public bool InItSpawn { get; set; } = true;

        [Category(Arceus), Description("Toggle true if the spawn comes out at night only")]
        public bool NightSpawn { get; set; } = true;

        [Category(Arceus), Description("Toggle true if spawn is 100% alpha.")]
        public bool SpawnIsStaticAlpha { get; set; } = false;

        [Category(Arceus), Description("Toggle true if spawn is in water. This is a little tricky as water encounters either go far away or hide under making it a little more difficult to encounter.")]
        public bool IsSpawnInWater { get; set; } = false;

        [Category(Arceus), Description("When enabled, the bot will stop the routine when match found.")]
        public bool HealInCamp { get; set; } = false;

        [Category(Arceus), Description("When enabled, the bot will stop the routine when match found.")]
        public bool StopOnMatch { get; set; } = false;
    }

    [Category(Arceus), TypeConverter(typeof(CategoryConverter<SpecialFiltersCategory>))]
    public class SpecialFiltersCategory
    {
        public override string ToString() => "Special Conditions";

        [Category(Arceus), Description("Toggle true if you want to run to the professor instead of teleport.")]
        public bool RunToProfessor { get; set; } = true;

        [Category(Arceus), Description("When enabled, the bot will use the Player Invincible cheat to not black out.")]
        public bool UsePlayerInvincibleCheat { get; set; } = true;

        [Category(Arceus), Description("Select the Location of the map you are hunting for. Ignore this setting if you are running MMOHunter")]
        public ArceusMap ScanLocation { get; set; } = ArceusMap.ObsidianFieldlands;

        [Category(Arceus), Description("Select the legend you are scanning for. If you are running GenieScanner, do not leave this as Heatran. Ignore this setting if you are running MMOHunter")]
        public LegendsOnMap TypeOfLegend { get; set; } = LegendsOnMap.Shaymin;

        [Category(Arceus), Description("Enter number of advances to search.")]
        public int MaxAdvancesToSearch { get; set; } = 50;

        [Category(Arceus), Description("Wait time between teleporting and scanning.")]
        public int WaitMsBetweenTeleports { get; set; } = 1000;

        [Category(Arceus), Description("Enter your player coordinates for Camp Location X Coordinate. Should be 8 characters long.")]
        public string CampZoneX { get; set; } = "";

        [Category(Arceus), Description("Enter your player coordinates for Camp Location Y Coordinate. Should be 8 characters long.")]
        public string CampZoneY { get; set; } = "";

        [Category(Arceus), Description("Enter your player coordinates for Camp Location Z Coordinate. Should be 8 characters long.")]
        public string CampZoneZ { get; set; } = "";

        [Category(Arceus), Description("Enter your player coordinates for Spawn Location X Coordinate. Should be 8 characters long.")]
        public string SpawnZoneX { get; set; } = "";

        [Category(Arceus), Description("Enter your player coordinates for Spawn Location Y Coordinate. Should be 8 characters long.")]
        public string SpawnZoneY { get; set; } = "";

        [Category(Arceus), Description("Enter your player coordinates for Spawn Location Z Coordinate. Should be 8 characters long.")]
        public string SpawnZoneZ { get; set; } = "";
    }

    public class CategoryConverter<T> : TypeConverter
    {
        public override bool GetPropertiesSupported(ITypeDescriptorContext? context) => true;

        public override PropertyDescriptorCollection GetProperties(ITypeDescriptorContext? context, object value, Attribute[]? attributes) => TypeDescriptor.GetProperties(typeof(T));

        public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType) => destinationType != typeof(string) && base.CanConvertTo(context, destinationType);
    }

    private int _completedShinyAlphasFound;

    [Category(Counts), Description("Arceus Encountered Pokémon")]
    public int CompletedShinyAlphasFound
    {
        get => _completedShinyAlphasFound;
        set => _completedShinyAlphasFound = value;
    }

    [Category(Counts), Description("When enabled, the counts will be emitted when a status check is requested.")]
    public bool EmitCountsOnStatusCheck { get; set; }

    public int AddCompletedShinyAlphaFound() => Interlocked.Increment(ref _completedShinyAlphasFound);

    public IEnumerable<string> GetNonZeroCounts()
    {
        if (!EmitCountsOnStatusCheck)
            yield break;
        if (CompletedShinyAlphasFound != 0)
            yield return $"Shiny Alphas Found: {CompletedShinyAlphasFound}";
    }
}
