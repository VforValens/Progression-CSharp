using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;

namespace ValensProgression;

/// <summary>
/// This is the replacement for the former package.json data. This is required for all mods.
///
/// This is where we define all the metadata associated with this mod.
/// You don't have to do anything with it, other than fill it out.
/// All properties must be overriden, properties you don't use may be left null.
/// It is read by the mod loader when this mod is loaded.
/// </summary>
public record ModMetadata : AbstractModMetadata
{
    /// <summary>
    /// Any string can be used for a modId, but it should ideally be unique and not easily duplicated
    /// a 'bad' ID would be: "mymod", "mod1", "questmod"
    /// It is recommended (but not mandatory) to use the reverse domain name notation,
    /// see: https://docs.oracle.com/javase/tutorial/java/package/namingpkgs.html
    /// </summary>
    public override string ModGuid { get; init; } = "com.sp-tarkov.valens.progression";

    public override string Name { get; init; } = "Valens Progression";
    public override string Author { get; init; } = "Valens";
    public override List<string>? Contributors { get; set; }
    public override string Version { get; init; } = "1.0.0";
    public override string SptVersion { get; init; } = "4.0.0";
    public override List<string>? LoadBefore { get; set; }
    public override List<string>? LoadAfter { get; set; }
    public override List<string>? Incompatibilities { get; set; }
    public override Dictionary<string, string>? ModDependencies { get; set; }
    public override string? Url { get; set; }
    public override bool? IsBundleMod { get; set; }
    public override string License { get; init; } = "CC-BY-NC-ND";
}

// We want to load after PostDBModLoader is complete, so we set our type priority to that, plus 1.
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 1)]
public class ValensProgression(
    ISptLogger<ValensProgression>
        logger, // We are injecting a logger similar to example 1, but notice the class inside <> is different
    DatabaseService databaseService,
    ConfigServer configServer,
    ModHelper modHelper)
    : IOnLoad // Implement the `IOnLoad` interface so that this mod can do something
{
    public ModConfig Config { get; set; }

    private readonly PmcConfig pmcConfig = configServer.GetConfig<PmcConfig>();
    private readonly BotConfig botConfig = configServer.GetConfig<BotConfig>();

    /// <summary>
    /// This is called when this class is loaded, the order in which it's loaded is set according to the type priority
    /// on the [Injectable] attribute on this class. Each class can then be used as an entry point to do
    /// things at varying times according to type priority
    /// </summary>
    public Task OnLoad()
    {
        // This will get us the full path to the mod, e.g. C:\spt\user\mods\5ReadCustomJsonConfig-0.0.1
        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());

        // We give the path to the mod folder and the file we want to get, giving us the config, supply the files 'type' between the diamond brackets
        Config = modHelper.GetJsonDataFromFile<ModConfig>(pathToMod, "config.json");

        // When SPT starts, it stores all the data found in (SPT_Data\Server\database) in memory.
        // We can use the 'databaseService' we injected to access this data. This includes files from EFT and SPT

        // Let's overwrite pmc bot generation.
        GeneratePmcs();

        // Let's write a nice log message to the server console so players know our mod has made changes
        logger.Success("Finished Progression Setup!");

        // Inform server we have finished
        return Task.CompletedTask;
    }

    private void GeneratePmcs()
    {
        // Set Bot Level Delta min and max.
        pmcConfig.BotRelativeLevelDeltaMin = 70;
        pmcConfig.BotRelativeLevelDeltaMax = 15;

        // Call changes to primary weapons.
        PrimaryWeaponChanges();

        // Call changes to the pmc config.
        PmcConfigChanges();
    }

    private void PrimaryWeaponChanges()
    {
        var bots = databaseService.GetBots();

        // Same as the above example, we use 'TryGetValue' to get the 'usec' bot and 'bear' bot (usec is the internal name for usec pmc's and same for bear)
        bots.Types.TryGetValue("usec", out var usecBot);
        bots.Types.TryGetValue("bear", out var bearBot);

        if (usecBot == null)
        {
            logger.Success("we fucked up");
            return;
        }

        // Get FirstPrimaryWeapons from each faction
        usecBot.BotInventory.Equipment.TryGetValue(EquipmentSlots.FirstPrimaryWeapon,
            out var usecFirstPrimaryWeapon);
        bearBot!.BotInventory.Equipment.TryGetValue(EquipmentSlots.FirstPrimaryWeapon,
            out var bearFirstPrimaryWeapon);

        // We access the first primary weapon dictionary by key directly using square brackets, we use ItemTpl to get the item ID
        // Alternately, we could have typed backPacks["59e763f286f7742ee57895da"] and done the same thing, ItemTpl makes it easier to read
        // ItemTpl makes it easier to read but worse for customization in JSON format
        logger.Error(usecFirstPrimaryWeapon.Count.ToString());

        usecFirstPrimaryWeapon.Clear();

        logger.Error(usecFirstPrimaryWeapon.Count.ToString());

        foreach (var weapon in Config.pmcEquipment.FirstPrimaryWeapon)
        {
            usecFirstPrimaryWeapon[weapon.Key] = weapon.Value;
            bearFirstPrimaryWeapon[weapon.Key] = weapon.Value;
            logger.Success($"Altered weapon {weapon.Key} with value {weapon.Value}");
        }

        logger.Error(usecFirstPrimaryWeapon.Count.ToString());

        // usecFirstPrimaryWeapon![ItemTpl.ASSAULTRIFLE_COLT_M4A1_556X45_ASSAULT_RIFLE] = 500;
        // bearFirstPrimaryWeapon![ItemTpl.ASSAULTRIFLE_COLT_M4A1_556X45_ASSAULT_RIFLE] = 500;
        logger.Success(usecFirstPrimaryWeapon[ItemTpl.ASSAULTRIFLE_COLT_M4A1_556X45_ASSAULT_RIFLE].ToString());
    }

    private void PmcConfigChanges()
    {
        var pmc = botConfig.Equipment["pmc"];

        if (pmc == null)
        {
            logger.Success("pmc is null check botconfig");
            return;
        }

        // Six total arrays of Level Range for Armor Plate Weighting.
        // The first level range of the array [0] is level 1-14
        if (pmc.ArmorPlateWeighting == null)
        {
            logger.Success("ArmorPlateWeighting is missing. Check botconfig");
            return;
        }

        pmc.ArmorPlateWeighting[0].LevelRange.Min = 1;
        pmc.ArmorPlateWeighting[0].LevelRange.Max = 14;


        // The second level range of the array [1] is level 11-26
        pmc.ArmorPlateWeighting[1].LevelRange.Min = 15;
        pmc.ArmorPlateWeighting[1].LevelRange.Max = 26;


        // The third level range of the array [2] is level 27-30
        pmc.ArmorPlateWeighting[2].LevelRange.Min = 27;
        pmc.ArmorPlateWeighting[2].LevelRange.Max = 30;


        // The fourth level range of the array [3] is level 31-34
        pmc.ArmorPlateWeighting[3].LevelRange.Min = 31;
        pmc.ArmorPlateWeighting[3].LevelRange.Max = 34;


        // The fifth level range of the array [4] is level 35-38
        pmc.ArmorPlateWeighting[4].LevelRange.Min = 35;
        pmc.ArmorPlateWeighting[4].LevelRange.Max = 38;

        // The sixth level range of the array [5] is level 39-42
        pmc.ArmorPlateWeighting[5].LevelRange.Min = 39;
        pmc.ArmorPlateWeighting[5].LevelRange.Max = 42;
    }
}

// This class should represent your config structure
public class ModConfig
{
    public Equipment pmcEquipment { get; set; }

    public class Equipment
    {
        public Dictionary<MongoId, int> FirstPrimaryWeapon { get; set; }
    }
}