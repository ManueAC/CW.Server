using CW.Server.Configuration;
using CW.Server.Data;
using CW.Server.Infrastructure;
using CW.Server.Storage;
using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;

namespace CW.Server.Services;




public sealed class MasteringService
{
    private readonly ICustomizationRepository _customization;
    private readonly IProfileRepository _profiles;
    private readonly ProfileFactory _factory;
    private readonly GameCatalog _catalog;
    private readonly ServerOptions _options;

    private readonly ILogger<MasteringService> _logger;
    private readonly IGameDataProvider _data;
    public MasteringService(
        ICustomizationRepository customization,
        IProfileRepository profiles,
        ProfileFactory factory,
        GameCatalog catalog,
        IOptions<ServerOptions> options,
        ILogger<MasteringService> logger,
        IGameDataProvider data
        )
    {
        _customization = customization;
        _profiles = profiles;
        _factory = factory;
        _catalog = catalog;
        _options = options.Value;
        _logger = logger;
        _data = data;

    }

    public JsonObject Load(int userId)
    {
        if (!_options.FreshAccounts || _options.UnlockAll)
        {
            return new JsonObject
            {
                ["user_id"] = userId.ToString(),
                ["mp"] = "9999",
                ["mp_exp"] = "999999",
                ["mod_states"] = _factory.BuildModStates(),
            };
        }

        var level = 0;

        if (userId != 0)
        {
            var data = _profiles.Get(userId).Obj("data");
            level = Json.ToInt(data["currentLevel"]);
        }

        var states = _factory.BuildFreshModStates();
        var stored = _customization.Mastering(userId);

        foreach (var weapon in stored.Obj("weapons"))
        {
            if (states[weapon.Key] is not JsonObject target)
            {
                continue;
            }

            var source = weapon.Value as JsonObject ?? new JsonObject();

            var exp = Json.ToInt(source["exp"]);

            if (exp != 0)
            {
                target["exp"] = exp;
            }

            var totalExp = Json.ToInt(source["total_exp"]);

            if (totalExp != 0)
            {
                target["total_exp"] = totalExp;
            }

            var weaponId = weapon.Key;
            var unavailableMeta = source.Obj("meta");
            var baseWeaponMetaLevelData = _data.Template("customization_main_load").Obj("weapon_info").Obj("data").Obj(weaponId);
            var baseWeaponData = _data.Backend("getWeaponsEn").Arr("weapons")[Json.ToInt(weaponId)];
            var metaLevelData = baseWeaponMetaLevelData.Obj("meta");


            var playerData = _profiles.Get(userId).Obj("data");

            var weaponTaskGoal = Json.ToInt(baseWeaponData?["Wtask"]?["count"]);
            var playerTaskCurrent = Json.ToInt(playerData?["weapons"]?[Json.ToInt(weaponId)]?["wtaskCurrent"]);

            var isP226 = Json.ToInt(weaponId) == 68;

            foreach (var metaLevel in metaLevelData)
            {
                var metaLevelInfo = metaLevel.Value;
                var mlExp = Json.ToInt(metaLevelInfo?["exp"]);
                // if (isP226)
                // {
                //     _logger.LogWarning("Metalevel required EXP={mlExp}/{totalExp}  metaLevelKey={metaLevel.Key} isMetalevelUnlocked={totalExp >= mlExp}",
                //         mlExp,
                //         totalExp,
                //         metaLevel.Key,
                //         // metaLevelInfo,
                //         totalExp >= mlExp
                //     );
                // }
                var shouldAllowUnlockOnPremium = Json.ToBool(baseWeaponData?["isPremium"]) ? true : playerTaskCurrent >= weaponTaskGoal;

                // _logger.LogInformation("Weapon {weapName} isPremium={isPremium}",
                //     baseWeaponData?["InterfaceName"],
                //     Json.ToBool(baseWeaponData?["isPremium"])
                // // totalExp, 
                // // mlExp
                // );

                // if (totalExp >= mlExp && Json.ToInt(metaLevel.Key) > 0 && playerTaskCurrent >= weaponTaskGoal)
                if (totalExp >= mlExp && Json.ToInt(metaLevel.Key) > 0 && shouldAllowUnlockOnPremium)
                {
                    var slots = metaLevelData.EnsureObject(metaLevel.Key).Obj("mods");
                    // if (isP226)
                    // {
                    //     _logger.LogInformation("MLKey={metaLevel.Key} totalExp={totalExp} mlExp={mlExp} isUnlocked", metaLevel.Key, totalExp, mlExp);
                    //     // _logger.LogInformation("SLOTS {slots}", slots);
                    // }

                    var playerMetaSlots = target.EnsureObject("meta").EnsureObject(metaLevel.Key);
                    // _logger.LogInformation("playerMetaSlots {playerMetaSlots}", playerMetaSlots);
                    foreach (var slot in slots)
                    {
                        // _logger.LogInformation("slot to Unlock: key={slot.Key}", slot.Key);
                        playerMetaSlots[slot.Key] = true;
                    }


                }
            }
            foreach (var metaLevel in source.Obj("meta"))
            {
                var slots = target.EnsureObject("meta").EnsureObject(metaLevel.Key);

                if (metaLevel.Value is JsonObject unlockedSlots)
                {
                    foreach (var slot in unlockedSlots)
                    {
                        slots[slot.Key] = true;
                    }
                }
            }

            foreach (var camo in source.Obj("camo"))
            {
                target.EnsureObject("camo")[camo.Key] = true;
            }
        }

        foreach (var suit in _customization.Get(userId).Obj("load_suits"))
        {
            if (suit.Value is not JsonObject weapons)
            {
                continue;
            }

            foreach (var weapon in weapons)
            {
                if (weapon.Value is not JsonObject entry)
                {
                    continue;
                }

                var camoId = entry["camo"];
                if (camoId is not null && states[weapon.Key] is JsonObject target)
                {
                    target.EnsureObject("camo")[Json.ToText(camoId)] = true;
                }
            }
        }

        var available = GameCatalog.MasteringPointGrant(level)
                        + Json.ToInt(stored["mp_earned"])
                        + Json.ToInt(stored["mp_bought"])
                        - Json.ToInt(stored["mp_spent"]);

        return new JsonObject
        {
            ["user_id"] = userId.ToString(),
            ["mp"] = Math.Max(0, available).ToString(),
            ["mp_exp"] = Json.ToInt(stored["mp_exp"]).ToString(),
            ["mod_states"] = states,
        };
    }

    public JsonObject WeaponStats(int userId, int weaponId)
    {
        var states = Load(userId).Obj("mod_states");

        if (states[weaponId.ToString()] is JsonObject stats)
        {
            return Json.CloneObject(stats);
        }

        return new JsonObject
        {
            ["exp"] = 0,
            ["total_exp"] = 0,
            ["meta"] = new JsonObject
            {
                ["-1"] = new JsonObject
                {
                    ["1"] = true,
                    ["2"] = true,
                    ["3"] = true,
                    ["4"] = true,
                },
            },
            ["camo"] = new JsonObject { ["60"] = true },
        };
    }

    public void GrantMasteringExp(int userId, int gain)
    {
        var perPoint = _catalog.MasteringExpPerPoint == 0 ? 5000 : _catalog.MasteringExpPerPoint;

        _customization.MutateMastering(userId, mastering =>
        {
            var total = Json.ToInt(mastering["mp_exp"]) + gain;
            mastering["mp_earned"] = Json.ToInt(mastering["mp_earned"]) + total / perPoint;
            mastering["mp_exp"] = total % perPoint;
        });
    }

    public IReadOnlyList<string> MetaSlots(int weaponId, string level)
    {
        if (_catalog.MetaSlots.TryGetValue(weaponId.ToString(), out var levels)
            && levels.TryGetValue(level, out var slots)
            && slots.Count > 0)
        {
            return slots;
        }

        return new[] { "1", "2", "3", "4" };
    }

    public int MetaCost(int weaponId, string level)
    {
        return _catalog.MetaGp.TryGetValue(weaponId.ToString(), out var levels)
               && levels.TryGetValue(level, out var cost)
            ? cost
            : 0;
    }

    public MasteringModLocation? LocateMod(int weaponId, int modId)
    {
        return _catalog.ModIndex.TryGetValue(weaponId.ToString(), out var mods)
               && mods.TryGetValue(modId.ToString(), out var location)
            ? location
            : null;
    }
}
