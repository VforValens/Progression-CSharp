using System.Reflection;
using SPTarkov.Common.Extensions;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Bots;
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
    private ModConfig _config { get; set; }
    private Bots _bots { get; set; }
    private BotType _usecBot { get; set; }
    private BotType _bearBot { get; set; }

    private readonly PmcConfig _pmcConfig = configServer.GetConfig<PmcConfig>();
    private readonly BotConfig _botConfig = configServer.GetConfig<BotConfig>();

    /// <summary>
    /// This is called when this class is loaded, the order in which it's loaded is set according to the type priority
    /// on the [Injectable] attribute on this class. Each class can then be used as an entry point to do
    /// things at varying times according to type priority
    /// </summary>
    public Task OnLoad()
    {
        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());

        _config = modHelper.GetJsonDataFromFile<ModConfig>(pathToMod, "config.json");

        GetBotInfo();
        
        if (_usecBot == null || _bearBot == null)
        {
            logger.Error("failed to retrieve usec or bot type from bot types");
            logger.Error("Failed to load Valens Progression. Stopped any further code changes");
            return Task.CompletedTask;
        }
        
        GeneratePmcs();

        // Let's write a nice log message to the server console so players know our mod has made changes
        logger.Success("Finished Loading Valens Progression!");

        // Inform server we have finished
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tarkov Median Dataset on Loyalty Level Distribution that we will follow for the modification.
    /// LL1 is levels 1-14
    /// LL2 is levels 15-26
    /// LL3 is levels 27-36
    /// LL4 is levels 37+
    /// Anything past 37+ should either be LL4 or better than, if possible to construct it with further than 4 level ranges as seen with armor plate weighting.
    /// </summary>
    
    private void GetBotInfo()
    {
        _bots = databaseService.GetBots();

        // Same as the above example, we use 'TryGetValue' to get the 'usec' bot and 'bear' bot (usec is the internal name for usec pmc's and same for bear)
        _bots.Types.TryGetValue("usec", out var usecBot);
        _bots.Types.TryGetValue("bear", out var bearBot);
        if (usecBot == null || bearBot == null)
        {
            logger.Error("failed to retrieve usec or bot type from bot types");
            return;
        }
        
        _usecBot = usecBot;
        _bearBot = bearBot;
    }

    private void GeneratePmcs()
    {
        // Set Bot Level Delta min and max.
        _pmcConfig.BotRelativeLevelDeltaMin = 70;
        _pmcConfig.BotRelativeLevelDeltaMax = 15;

        // Call changes to PMC equipment
        PmcEquipmentChanges();

        PmcAmmoWeighting();

        // Call changes to the pmc config.
        PmcConfigChanges();
        
        // Call changes to the loyalty levels.
        LoyaltyLevelChanges();
    }

    private void PmcAmmoWeighting()
    {
        foreach (var ammoType in _config.pmcAmmo.GetAllPropsAsDict())
        {
            // AmmoType.Key is name of ammo
            // AmmoType.Value is mongoid + double
            // if ammotype is not in correct format skip
            if (ammoType.Value is not Dictionary<MongoId, double> ammo)
            {
                logger.Error("couldn't parse ammo details");
                continue;
            }
            
            // Find matching ammo type to bot
            _usecBot.BotInventory.Ammo.TryGetValue(ammoType.Key, out var botAmmo);

            if (botAmmo is null)
            {
                logger.Error($" UsecBot or BearBot is missing ammo type {ammoType.Key}");
                continue;
            }

            botAmmo.Clear();
            foreach (var ourAmmo in ammo)
            {
                botAmmo[ourAmmo.Key] = ourAmmo.Value;
            }
        }
    }

    private void PmcEquipmentChanges()
    {
        foreach (var equipmentSlot in _config.pmcEquipment.GetAllPropsAsDict())
        {
            Enum.TryParse(equipmentSlot.Key, out EquipmentSlots matchedSlot);

            if (equipmentSlot.Value is not Dictionary<MongoId, double> equipment)
            {
                logger.Error("couldn't parse equipment details");
                continue;
            }
            
            // Get Requested Equipmentslot
            _usecBot.BotInventory.Equipment.TryGetValue(matchedSlot, out var usecEquipmentSlot);
            _bearBot.BotInventory.Equipment.TryGetValue(matchedSlot, out var bearEquipmentSlot);

            usecEquipmentSlot.Clear();
            bearEquipmentSlot.Clear();

            foreach (var jsonEquipment in equipment)
            {
                usecEquipmentSlot[jsonEquipment.Key] = jsonEquipment.Value;
                bearEquipmentSlot[jsonEquipment.Key] = jsonEquipment.Value;
            }
            logger.Warning($"Adjusted {matchedSlot.ToString()} values");
        }
    }
    
    private void PmcConfigChanges()
    {
        var pmc = _botConfig.Equipment["pmc"];

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

        // TO DO:: !!!!! ACTUALLY CUSTOMIZE THE ARMOR PLATE VALUES !!!!!
        // Level Range 1-14 or Loyalty Level 1 armor plate changes
        pmc.ArmorPlateWeighting[0].LevelRange.Min = 1;
        pmc.ArmorPlateWeighting[0].LevelRange.Max = 14;
        pmc.ArmorPlateWeighting[0].Values["back_plate"] = new Dictionary<string, double>()
            { { "2", 12 }, { "3", 77 }, { "4", 8 }, { "5", 2 }, { "6", 1 } };
        pmc.ArmorPlateWeighting[0].Values["front_plate"] = new Dictionary<string, double>()
            { { "2", 12 }, { "3", 77 }, { "4", 8 }, { "5", 2 }, { "6", 1 } };
        pmc.ArmorPlateWeighting[0].Values["left_side_plate"] = new Dictionary<string, double>()
            { { "2", 12 }, { "3", 77 }, { "4", 8 }, { "5", 2 }, { "6", 1 } };
        pmc.ArmorPlateWeighting[0].Values["right_side_plate"] = new Dictionary<string, double>()
            { { "2", 12 }, { "3", 77 }, { "4", 8 }, { "5", 2 }, { "6", 1 } };

        // TO DO:: !!!!! ACTUALLY CUSTOMIZE THE ARMOR PLATE VALUES !!!!!
        // Level Range 15-26 or Loyalty Level 2 armor plate changes
        pmc.ArmorPlateWeighting[1].LevelRange.Min = 15;
        pmc.ArmorPlateWeighting[1].LevelRange.Max = 26;
        pmc.ArmorPlateWeighting[0].Values["back_plate"] = new Dictionary<string, double>()
            { { "2", 25 }, { "3", 20 }, { "4", 5 }, { "5", 1 }, { "6", 1 } };
        pmc.ArmorPlateWeighting[0].Values["front_plate"] = new Dictionary<string, double>()
            { { "2", 25 }, { "3", 20 }, { "4", 5 }, { "5", 1 }, { "6", 1 } };
        pmc.ArmorPlateWeighting[0].Values["left_side_plate"] = new Dictionary<string, double>()
            { { "2", 25 }, { "3", 20 }, { "4", 5 }, { "5", 1 }, { "6", 1 } };
        pmc.ArmorPlateWeighting[0].Values["right_side_plate"] = new Dictionary<string, double>()
            { { "2", 25 }, { "3", 20 }, { "4", 5 }, { "5", 1 }, { "6", 1 } };
        
        // TO DO:: !!!!! ACTUALLY CUSTOMIZE THE ARMOR PLATE VALUES !!!!!
        // Level Range 27-36 or Loyalty Level 3 armor plate changes
        pmc.ArmorPlateWeighting[2].LevelRange.Min = 27;
        pmc.ArmorPlateWeighting[2].LevelRange.Max = 36;
        pmc.ArmorPlateWeighting[0].Values["back_plate"] = new Dictionary<string, double>()
            { { "2", 25 }, { "3", 20 }, { "4", 5 }, { "5", 1 }, { "6", 1 } };
        pmc.ArmorPlateWeighting[0].Values["front_plate"] = new Dictionary<string, double>()
            { { "2", 25 }, { "3", 20 }, { "4", 5 }, { "5", 1 }, { "6", 1 } };
        pmc.ArmorPlateWeighting[0].Values["left_side_plate"] = new Dictionary<string, double>()
            { { "2", 25 }, { "3", 20 }, { "4", 5 }, { "5", 1 }, { "6", 1 } };
        pmc.ArmorPlateWeighting[0].Values["right_side_plate"] = new Dictionary<string, double>()
            { { "2", 25 }, { "3", 20 }, { "4", 5 }, { "5", 1 }, { "6", 1 } };

        // TO DO:: !!!!! ACTUALLY CUSTOMIZE THE ARMOR PLATE VALUES !!!!!
        // Level Range 37-43 or Loyalty Level 4 armor plate changes
        pmc.ArmorPlateWeighting[3].LevelRange.Min = 37;
        pmc.ArmorPlateWeighting[3].LevelRange.Max = 43;
        pmc.ArmorPlateWeighting[0].Values["back_plate"] = new Dictionary<string, double>()
            { { "2", 25 }, { "3", 20 }, { "4", 5 }, { "5", 1 }, { "6", 1 } };
        pmc.ArmorPlateWeighting[0].Values["front_plate"] = new Dictionary<string, double>()
            { { "2", 25 }, { "3", 20 }, { "4", 5 }, { "5", 1 }, { "6", 1 } };
        pmc.ArmorPlateWeighting[0].Values["left_side_plate"] = new Dictionary<string, double>()
            { { "2", 25 }, { "3", 20 }, { "4", 5 }, { "5", 1 }, { "6", 1 } };
        pmc.ArmorPlateWeighting[0].Values["right_side_plate"] = new Dictionary<string, double>()
            { { "2", 25 }, { "3", 20 }, { "4", 5 }, { "5", 1 }, { "6", 1 } };

        // TO DO:: !!!!! ACTUALLY CUSTOMIZE THE ARMOR PLATE VALUES !!!!!
        // The fifth level range of the array [4] is level 44-49
        pmc.ArmorPlateWeighting[4].LevelRange.Min = 44;
        pmc.ArmorPlateWeighting[4].LevelRange.Max = 49;
        pmc.ArmorPlateWeighting[0].Values["back_plate"] = new Dictionary<string, double>()
            { { "2", 25 }, { "3", 20 }, { "4", 5 }, { "5", 1 }, { "6", 1 } };
        pmc.ArmorPlateWeighting[0].Values["front_plate"] = new Dictionary<string, double>()
            { { "2", 25 }, { "3", 20 }, { "4", 5 }, { "5", 1 }, { "6", 1 } };
        pmc.ArmorPlateWeighting[0].Values["left_side_plate"] = new Dictionary<string, double>()
            { { "2", 25 }, { "3", 20 }, { "4", 5 }, { "5", 1 }, { "6", 1 } };
        pmc.ArmorPlateWeighting[0].Values["right_side_plate"] = new Dictionary<string, double>()
            { { "2", 25 }, { "3", 20 }, { "4", 5 }, { "5", 1 }, { "6", 1 } };

        // TO DO:: !!!!! ACTUALLY CUSTOMIZE THE ARMOR PLATE VALUES !!!!!
        // The sixth level range of the array [5] is level 50-100
        pmc.ArmorPlateWeighting[5].LevelRange.Min = 50;
        pmc.ArmorPlateWeighting[5].LevelRange.Max = 100;
        pmc.ArmorPlateWeighting[0].Values["back_plate"] = new Dictionary<string, double>()
            { { "2", 25 }, { "3", 20 }, { "4", 5 }, { "5", 1 }, { "6", 1 } };
        pmc.ArmorPlateWeighting[0].Values["front_plate"] = new Dictionary<string, double>()
            { { "2", 25 }, { "3", 20 }, { "4", 5 }, { "5", 1 }, { "6", 1 } };
        pmc.ArmorPlateWeighting[0].Values["left_side_plate"] = new Dictionary<string, double>()
            { { "2", 25 }, { "3", 20 }, { "4", 5 }, { "5", 1 }, { "6", 1 } };
        pmc.ArmorPlateWeighting[0].Values["right_side_plate"] = new Dictionary<string, double>()
            { { "2", 25 }, { "3", 20 }, { "4", 5 }, { "5", 1 }, { "6", 1 } };
    }

    private void LoyaltyLevelChanges()
    {
        var pmc = _botConfig.Equipment["pmc"];

        // Progression Whitelist LL1
        pmc?.Randomisation?.Clear();

        if (pmc != null)
        {
            pmc.FaceShieldIsActiveChancePercent = 100;
            pmc.LaserIsActiveChancePercent = 100;
            pmc.LightIsActiveDayChancePercent = 15;
            pmc.LightIsActiveNightChancePercent = 95;
            pmc.NvgIsActiveChanceDayPercent = 5;
            pmc.NvgIsActiveChanceNightPercent = 100;
        }

        var progressionWhitelistLL1 = new RandomisationDetails()
        {
            LevelRange = new MinMax<int>(1, 14),
            RandomisedWeaponModSlots = [],
            Generation = new Dictionary<string, GenerationData>()
            {
                {
                    "backpackLoot", new GenerationData()
                    {
                            Weights = new Dictionary<double, double>()
                            {
                                { 0, 4 },
                                { 1, 15 },
                                { 10, 1 },
                                { 2, 40 },
                                { 3, 10 },
                                { 4, 8 },
                                { 5, 2 }
                            },
                            Whitelist = new Dictionary<MongoId, double>()
                            {
                            }
                    }
                },
                {
                    "drugs", new GenerationData()
                    {
                        Weights = new Dictionary<double, double>()
                        {
                            { 0, 1 },
                            { 1, 1 }
                        },
                        Whitelist = new Dictionary<MongoId, double>()
                        {
                        }
                    }
                },
                {
                  "grenades", new GenerationData()
                  {
                      Weights = new Dictionary<double, double>()
                      {
                          { 0, 2 },
                          { 1, 1 }
                      },
                      Whitelist = new Dictionary<MongoId, double>()
                      {
                          { new MongoId("5448be9a4bdc2dfd2f8b456a"), 24 },
                          { new MongoId("5710c24ad2720bc3458b45a3"), 24 },
                          { new MongoId("58d3db5386f77426186285a0"), 24 },
                          { new MongoId("5a0c27731526d80618476ac4"), 24 },
                          { new MongoId("5e340dcdcb6d5863cc5e5efb"), 5 }
                      }
                  }
                },
                {
                    "healing", new GenerationData()
                    {
                        Weights = new Dictionary<double, double>()
                        {
                            { 0, 5 },
                            { 1, 60 },
                            { 2, 35 }
                        },
                        Whitelist = new Dictionary<MongoId, double>()
                        {
                            { new MongoId("544fb3364bdc2d34748b456a"), 1 },
                            { new MongoId("5755356824597772cb798962"), 1 },
                            { new MongoId("590c661e86f7741e566b646a"), 1 },
                            { new MongoId("544fb37f4bdc2dee738b4567"), 1 },
                            { new MongoId("5e831507ea0a7c419c2f9bd9"), 1 },
                            { new MongoId("544fb25a4bdc2dfb738b4567"), 1 }
                        }
                    }
                },
                {
                    "magazines", new GenerationData()
                    {
                        Weights = new Dictionary<double, double>()
                        {
                            { 0, 0 },
                            { 1, 45 },
                            { 2, 45 },
                            { 3, 10 }
                        },
                        Whitelist = new Dictionary<MongoId, double>()
                        {
                        }
                    }
                },
                {
                    "pocketLoot", new GenerationData()
                    {
                        Weights = new Dictionary<double, double>()
                        {
                            { 0, 38 },
                            { 1, 60 },
                            { 2, 1 },
                            { 3, 1 }
                        },
                        Whitelist = new Dictionary<MongoId, double>()
                        {
                        }
                    }
                },
                {
                    "stims", new GenerationData()
                    {
                        Weights = new Dictionary<double, double>()
                        {
                            { 0, 93 },
                            { 1, 7 }
                        },
                        Whitelist = new Dictionary<MongoId, double>()
                        {
                        }
                    }
                },
                {
                    "vestLoot", new GenerationData()
                    {
                        Weights = new Dictionary<double, double>()
                        {
                            { 0, 2 },
                            { 1, 12 },
                            { 2, 1 },
                            { 3, 1 },
                            { 4, 1 }
                        },
                        Whitelist = new Dictionary<MongoId, double>()
                        {
                        }
                    }
                },
            },
            Equipment = new Dictionary<string, double>()
            {
                { "ArmBand", 90 },
                { "Backpack", 65 },
                { "Earpiece", 50 },
                { "Eyewear", 5 },
                { "FaceCover", 50 },
                { "FirstPrimaryWeapon", 95 },
                { "Holster", 5 },
                { "SecondPrimaryWeapon", 5 },
                { "TacticalVest", 100 }
            },
            WeaponMods = new Dictionary<string, double>()
            {
                { "mod_barrel", 5 },
                { "mod_bipod", 10 },
                { "mod_equipment", 5 },
                { "mod_equipment_000", 10 },
                { "mod_equipment_001", 5 },
                { "mod_equipment_002", 0 },
                { "mod_flashlight", 10 },
                { "mod_foregrip", 10 },
                { "mod_handguard", 10 },
                { "mod_launcher", 0 },
                { "mod_magazine", 10 },
                { "mod_mount", 15 },
                { "mod_mount_000", 10 },
                { "mod_mount_001", 10 },
                { "mod_mount_002", 10 },
                { "mod_mount_003", 10 },
                { "mod_mount_004", 10 },
                { "mod_mount_005", 10 },
                { "mod_mount_006", 10 },
                { "mod_muzzle", 10 },
                { "mod_muzzle_000", 10 },
                { "mod_muzzle_001", 10 },
                { "mod_nvg", 0 },
                { "mod_pistol_grip", 10 },
                { "mod_pistol_grip_akms", 10 },
                { "mod_reciever", 10 },
                { "mod_scope", 10 },
                { "mod_scope_000", 15 },
                { "mod_scope_001", 15 },
                { "mod_scope_002", 15 },
                { "mod_scope_003", 15 },
                { "mod_tactical", 10 },
                { "mod_tactical001", 10 },
                { "mod_tactical002", 10 },
                { "mod_tactical_000", 10 },
                { "mod_tactical_001", 10 },
                { "mod_tactical_002", 10 },
                { "mod_tactical_003", 10 },
                { "mod_tactical_2", 10 }
            },
            EquipmentMods = new Dictionary<string, double>()
            {
                { "back_plate", 100 },
                { "left_side_plate", 0 },
                { "right_side_plate", 0 },
                { "mod_equipment", 3 },
                { "mod_equipment_000", 3 },
                { "mod_equipment_001", 3 },
                { "mod_equipment_002", 3 },
                { "mod_equipment_003", 3 },
                { "mod_mount", 1 },
                { "mod_nvg", 3 }
            },
            NighttimeChanges = new NighttimeChanges
                {
                    EquipmentModsModifiers = new Dictionary<string, float>()
                    {
                        { "mod_nvg", 30 }
                    }
                },
            MinimumMagazineSize = null
        };
    }

}

// This class should represent your config structure
public class ModConfig
{
    public Equipment pmcEquipment { get; set; }
    public Ammo pmcAmmo { get; set; }
    
    public class Equipment
    {
        public Dictionary<MongoId, double> FirstPrimaryWeapon { get; set; }
        public Dictionary<MongoId, double> Holster { get; set; }
        public Dictionary<MongoId, double> ArmorVest { get; set; }
        public Dictionary<MongoId, double> Backpack { get; set; }
        public Dictionary<MongoId, double> Eyewear { get; set; }
        public Dictionary<MongoId, double> FaceCover { get; set; }
        public Dictionary<MongoId, double> Headwear { get; set; }
        public Dictionary<MongoId, double> Earpiece { get; set; }
        public Dictionary<MongoId, double> TacticalVest { get; set; }
        public Dictionary<MongoId, double> ArmBand { get; set; }
    }

    public class Ammo
    {
        public Dictionary<MongoId, double> Caliber40x46 { get; set; }
        public Dictionary<MongoId, double> Caliber127x55 { get; set; }
        public Dictionary<MongoId, double> Caliber86x70 { get; set; }
        public Dictionary<MongoId, double> Caliber762x54R { get; set; }
        public Dictionary<MongoId, double> Caliber762x51 { get; set; }
        public Dictionary<MongoId, double> Caliber762x39 { get; set; }
        public Dictionary<MongoId, double> Caliber762x35 { get; set; }
        public Dictionary<MongoId, double> Caliber762x25TT { get; set; }
        public Dictionary<MongoId, double> Caliber68x51 { get; set; }
        public Dictionary<MongoId, double> Caliber366TKM { get; set; }
        public Dictionary<MongoId, double> Caliber556x45NATO { get; set; }
        public Dictionary<MongoId, double> Caliber545x39 { get; set; }
        public Dictionary<MongoId, double> Caliber57x28 { get; set; }
        public Dictionary<MongoId, double> Caliber46x30 { get; set; }
        public Dictionary<MongoId, double> Caliber9x18PM { get; set; }
        public Dictionary<MongoId, double> Caliber9x19PARA { get; set; }
        public Dictionary<MongoId, double> Caliber9x21 { get; set; }
        public Dictionary<MongoId, double> Caliber9x39 { get; set; }
        public Dictionary<MongoId, double> Caliber9x33R { get; set; }
        public Dictionary<MongoId, double> Caliber1143x23ACP { get; set; }
        public Dictionary<MongoId, double> Caliber12g { get; set; }
        public Dictionary<MongoId, double> Caliber23x75 { get; set; }
    }
}

