using CW.Server.Configuration;
using CW.Server.Data;
using CW.Server.Infrastructure;
using CW.Server.Services;
using CW.Server.Storage;
using CW.Server.Transport;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace CW.Server.Endpoints;

public sealed class AccountEndpoints
{
    private readonly IAccountRepository _accounts;
    private readonly ISessionRegistry _sessions;
    private readonly IProfileRepository _profiles;
    private readonly IClanRepository _clans;
    private readonly ICustomizationRepository _customization;
    private readonly IAttemptsRepository _attempts;
    private readonly IHostRegistry _hosts;
    private readonly PlayerService _players;
    private readonly ProfileNormalizer _normalizer;
    private readonly Data.GameCatalog _catalog;
    private readonly ServerOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<AccountEndpoints> _logger;
    private readonly IGameDataProvider _data;
    private readonly RouletteEndpoints _roulette;

    public AccountEndpoints(
        IAccountRepository accounts,
        ISessionRegistry sessions,
        IProfileRepository profiles,
        IClanRepository clans,
        ICustomizationRepository customization,
        IAttemptsRepository attempts,
        IHostRegistry hosts,
        PlayerService players,
        ProfileNormalizer normalizer,
        Data.GameCatalog catalog,
        IOptions<ServerOptions> options,
        IClock clock,
        ILogger<AccountEndpoints> logger,
        IGameDataProvider data,
        RouletteEndpoints roulette
        )
    {
        _accounts = accounts;
        _sessions = sessions;
        _profiles = profiles;
        _clans = clans;
        _customization = customization;
        _attempts = attempts;
        _roulette = roulette;
        _hosts = hosts;
        _players = players;
        _normalizer = normalizer;
        _catalog = catalog;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
        _data = data;
    }

    public JsonNode Init(LegacyRequest request)
    {
        var email = request.Text("email").Trim();
        var password = request.Text("password");

        var login = _accounts.LoginOrRegister(email, password);
        var token = _sessions.Create(login.UserId);
        _profiles.Get(login.UserId, _accounts.ByUserId(login.UserId)?.Nick);

        if (login.Created)
        {
            _logger.LogInformation(
                "registered account {Email} as user_id {UserId} ({Mode})",
                email,
                login.UserId,
                _options.FreshAccounts ? "fresh" : "maxed");
        }

        return new JsonObject
        {
            ["data"] = true,
            ["hash"] = RandomHash(),
            ["result"] = 0,
            ["ss"] = token,
            ["message"] = null,
            ["time"] = _clock.UnixSeconds.ToString(),
            ["user_id"] = login.UserId,
            ["updated"] = true,
            ["sessionHash"] = RandomHash(),
        };
    }

    public JsonNode Load(LegacyRequest request)
    {
        var userId = _players.Caller(request);
        if (userId is null)
        {
            return Reply.Fail("no session");
        }

        var profile = _profiles.Get(userId.Value);

        var globals = _data.Backend("getGlobals");

        // Lista de armas que NO deben desbloquearse (excepciones)
        var weaponExceptions = new List<string> { "tkpd1", "tkpds", "ksg" };

        // Obtener índices desbloqueados
        var unlockedSets = _players.Data(userId.Value)?["unlockedSets"]?.AsArray();
        var unlockedBlockIndices = unlockedSets?
            .Select((value, index) => new { Value = value.GetValue<int>(), Index = index })
            .Where(x => x.Value == 1)
            .Select(x => x.Index)
            .ToList();

        // Filtrar armas por block y excluir las que están en weaponExceptions
        var allWeapons = _data.Backend("getWeaponsEn").Arr("weapons");
        var filteredWeapons = allWeapons
            .Where(weapon =>
            {
                var block = weapon["block"].GetValue<int>();
                var type = weapon["type"].GetValue<string>();

                // Debe estar en un bloque desbloqueado Y NO estar en la lista de excepciones
                return unlockedBlockIndices.Contains(block) && !weaponExceptions.Contains(type);
            })
            .ToArray();

        _logger.LogInformation($"[GetAttempts] userId: {userId} unlockedBlocks: [{string.Join(",", unlockedBlockIndices)}], filteredWeapons count: {filteredWeapons.Length} (excluidas: {weaponExceptions.Count})");

        var premiumWeaponsWithIndex = filteredWeapons
     .Select((weapon, index) => new { Weapon = weapon, Index = index })
     .Where(x =>
     {
         // Verificar que el campo "isPremium" exista y sea true
         var isPremiumField = x.Weapon["isPremium"];
         return isPremiumField != null && isPremiumField.GetValue<bool>() == true;
     })
     .ToList();

        if (premiumWeaponsWithIndex.Count > 0)
        {
            // 2. Seleccionar un arma aleatoria de la lista de premium
            var randomIndex = Random.Shared.Next(0, premiumWeaponsWithIndex.Count);
            var selectedWeapon = premiumWeaponsWithIndex[randomIndex];

            // 3. El ID será el índice de esta arma dentro del array filteredWeapons
            int weaponId = selectedWeapon.Index;

            // 4. Generar un descuento aleatorio (ejemplo: entre 10% y 90%)
            // Puedes ajustar este rango según tus necesidades (ej. Random.Shared.Next(50, 90) para 50-89%)
            int randomDiscount = Random.Shared.Next(65, 90);

            // 5. Actualizar el perfil del USUARIO (no el -999) con el discount_id y el discount
            _players.MutateData(userId.Value, (userData) =>
            {
                userData["discount_id"] = weaponId;
                userData["discount"] = randomDiscount;
            });

            _logger.LogInformation($"[GetAttempts] userId: {userId} Premium weapon assigned! ID: {weaponId}, Discount: {randomDiscount}%");
        }

        return _options.UnlockAll
            ? ProfileFactory.ApplyUnlockAll(Json.CloneObject(profile))
            : profile;
    }

    public JsonNode Save(LegacyRequest request)
    {
        var userId = _players.Caller(request);

        if (userId is not null && request.Body is JsonObject body)
        {
            var profile = _profiles.Get(userId.Value);
            var incoming = body.Obj("data");
            var target = profile.EnsureObject("data");

            foreach (var key in ClientOwnedFields)
            {
                if (incoming.TryGetPropertyValue(key, out var value))
                {
                    target[key] = Json.Clone(value);
                }
            }

            _profiles.Save(userId.Value, profile);
        }

        return new JsonObject { ["data"] = true, ["result"] = 0, ["message"] = null };
    }

    public JsonNode IdLoad(LegacyRequest request)
    {
        var userId = _players.Caller(request);
        if (userId is null)
        {
            return Reply.Fail("unknown user");
        }

        var profile = Json.CloneObject(_profiles.Get(userId.Value));
        profile["result"] = 0;

        if (_options.UnlockAll)
        {
            ProfileFactory.ApplyUnlockAll(profile);
        }

        if (profile["data"] is JsonObject data)
        {
            data["permission"] = 3;
        }

        return profile;
    }

    public JsonNode IdSave(LegacyRequest request)
    {
        var userId = _players.Caller(request);

        if (userId is null || request.Body is not JsonObject body || body.Count == 0)
        {
            return new JsonObject { ["data"] = true, ["result"] = 0 };
        }

        var stored = _profiles.Get(userId.Value);
        var priorEarn = Json.ToInt(stored.Obj("data")["clan_earn"]);
        var merged = Json.CloneObject(stored);

        foreach (var pair in body)
        {
            if (pair.Key == "data" && pair.Value is JsonObject incoming && merged["data"] is JsonObject target)
            {
                foreach (var field in incoming)
                {
                    if (field.Key != "podgon")
                    {
                        target[field.Key] = Json.Clone(field.Value);
                    }
                }
            }
            else
            {
                merged[pair.Key] = Json.Clone(pair.Value);
            }
        }

        merged["result"] = 0;
        _normalizer.Normalize(merged);

        var mergedData = merged.Obj("data");
        var deltaXp = Json.ToInt(mergedData["delta_xp"]);
        var deltaCr = Json.ToInt(mergedData["delta_cr"]);
        var share = Json.ToDouble(mergedData["clan_earn_proc"]);

        var earnedExp = deltaXp > 0 && share > 0 ? (int)(deltaXp * share) : 0;
        var earnedCr = deltaCr > 0 && share > 0 ? (int)(deltaCr * share) : 0;

        if (earnedExp > 0 && merged["data"] is JsonObject writable)
        {
            writable["clan_earn"] = priorEarn + earnedExp;
        }

        _profiles.Save(userId.Value, merged);

        if (earnedExp > 0 || earnedCr > 0)
        {
            var (clanId, clan) = _clans.ClanOf(userId.Value);

            if (clan is not null && clanId is not null)
            {
                _clans.Mutate(data =>
                {
                    if (data[clanId] is not JsonObject entry)
                    {
                        return;
                    }

                    entry["clan_exp"] = Json.ToInt(entry["clan_exp"]) + earnedExp;
                    entry["clan_cr"] = Json.ToInt(entry["clan_cr"]) + earnedCr;
                });

                _logger.LogInformation(
                    "clan {ClanId} credited +{Exp} exp +{Credits} cr from uid{UserId}",
                    clanId,
                    earnedExp,
                    earnedCr,
                    userId.Value);
            }
        }

        return new JsonObject { ["data"] = true, ["result"] = 0 };
    }

    public JsonNode KeepAlive(LegacyRequest request)
    {
        return new JsonObject { ["data"] = true, ["result"] = 0 };
    }

    public JsonNode GetAttempts(LegacyRequest request)
    {
        var userId = _players.Caller(request) ?? 0;
        _logger.LogWarning("[ GetAttempts ] userId: {userId} dailyAttempts={_catalog.DailyAttempts}", userId, _catalog.DailyAttempts);

        var globals = _data.Backend("getGlobals");

        //     // Lista de armas que NO deben desbloquearse (excepciones)
        //     var weaponExceptions = new List<string> { "tkpd1", "tkpds", "ksg" };

        //     // Obtener índices desbloqueados
        //     var unlockedSets = _players.Data(userId)["unlockedSets"].AsArray();
        //     var unlockedBlockIndices = unlockedSets
        //         .Select((value, index) => new { Value = value.GetValue<int>(), Index = index })
        //         .Where(x => x.Value == 1)
        //         .Select(x => x.Index)
        //         .ToList();

        //     // Filtrar armas por block y excluir las que están en weaponExceptions
        //     var allWeapons = _data.Backend("getWeaponsEn").Arr("weapons");
        //     var filteredWeapons = allWeapons
        //         .Where(weapon =>
        //         {
        //             var block = weapon["block"].GetValue<int>();
        //             var type = weapon["type"].GetValue<string>();

        //             // Debe estar en un bloque desbloqueado Y NO estar en la lista de excepciones
        //             return unlockedBlockIndices.Contains(block) && !weaponExceptions.Contains(type);
        //         })
        //         .ToArray();

        //     _logger.LogInformation($"[GetAttempts] userId: {userId} unlockedBlocks: [{string.Join(",", unlockedBlockIndices)}], filteredWeapons count: {filteredWeapons.Length} (excluidas: {weaponExceptions.Count})");

        //     // ========================================================================
        //     // NUEVA LÓGICA: Seleccionar arma premium aleatoria y aplicar descuento
        //     // ========================================================================

        //     // var premiumWeaponsWithIndex = filteredWeapons
        //     //     .Select((weapon, index) => new { Weapon = weapon, Index = index })
        //     //     .Where(x => x.Weapon["isPremium"]?.GetValue<bool>() == true) // Usamos '?' porque isPremium es opcional en el JSON
        //     //     .ToList();
        //     // 1. Filtrar solo las armas premium y conservar su índice original en filteredWeapons
        //     var premiumWeaponsWithIndex = filteredWeapons
        //  .Select((weapon, index) => new { Weapon = weapon, Index = index })
        //  .Where(x =>
        //  {
        //      // Verificar que el campo "isPremium" exista y sea true
        //      var isPremiumField = x.Weapon["isPremium"];
        //      return isPremiumField != null && isPremiumField.GetValue<bool>() == true;
        //  })
        //  .ToList();

        //     if (premiumWeaponsWithIndex.Count > 0)
        //     {
        //         // 2. Seleccionar un arma aleatoria de la lista de premium
        //         var randomIndex = Random.Shared.Next(0, premiumWeaponsWithIndex.Count);
        //         var selectedWeapon = premiumWeaponsWithIndex[randomIndex];

        //         // 3. El ID será el índice de esta arma dentro del array filteredWeapons
        //         int weaponId = selectedWeapon.Index;

        //         // 4. Generar un descuento aleatorio (ejemplo: entre 10% y 90%)
        //         // Puedes ajustar este rango según tus necesidades (ej. Random.Shared.Next(50, 90) para 50-89%)
        //         int randomDiscount = Random.Shared.Next(65, 90);

        //         // 5. Actualizar el perfil del USUARIO (no el -999) con el discount_id y el discount
        //         _players.MutateData(userId, (userData) =>
        //         {
        //             userData["discount_id"] = weaponId;
        //             userData["discount"] = randomDiscount;
        //         });

        //         // var remaining = Math.Max(0,  - 1);


        //         _logger.LogInformation($"[GetAttempts] userId: {userId} Premium weapon assigned! ID: {weaponId}, Discount: {randomDiscount}%");
        //     }
        // ========================================================================


        // Actualizar profile -999.json con las armas desbloqueadas
        // _players.MutateData(-999, (data) =>
        // {
        //     var weaponsArray = new JsonArray();

        //     for (int i = 0; i < filteredWeapons.Length; i++)
        //     {
        //         var weapon = filteredWeapons[i];
        //         var weaponType = weapon["type"].GetValue<string>();

        //         weaponsArray.Add(new JsonObject
        //         {
        //             ["unlocked"] = true,
        //             ["wtaskCurrent"] = 0,
        //             ["repair_info"] = 0,
        //             ["rentEnd"] = -1,
        //             // ["id"] = i,
        //             ["wtaskMax"] = 150,
        //             ["wtaskSelected"] = false
        //             // ["type"] = weaponType // Si necesitas guardar el tipo también
        //         });
        //     }

        //     data["weapons"] = weaponsArray;
        // });

        var dailyRouletteCount = globals["roulette"]?["dailyCount"]?.GetValue<int>() ?? 0;
        _logger.LogInformation($"[GetAttempts] userId: {userId} Daily roulette attempts assigned!");

        _attempts.Set(userId, dailyRouletteCount);
        return Reply.Ok(("attempts", _attempts.Get(userId, _catalog.DailyAttempts)));
    }

    public JsonNode RecordFriends(LegacyRequest request)
    {
        var userId = _players.Caller(request);

        if (userId is not null && request.Body is not null)
        {
            _customization.SaveFriends(userId.Value, request.Body);
        }

        return Reply.Ok();
    }

    public JsonNode ContentInfo(LegacyRequest request)
    {
        return new JsonObject { ["result"] = 0, ["server"] = _options.CdnHost };
    }

    public JsonNode CheckNick(LegacyRequest request)
    {
        var nick = request.Text("nick").Trim();

        if (nick.Length < 3 || _accounts.NickTaken(nick))
        {
            return new JsonObject { ["result"] = 0, ["error"] = "Nickname exist" };
        }

        return Reply.Fail("failed");
    }

    public JsonNode GetHosts(LegacyRequest request)
    {
        return HostList();
    }

    public JsonNode MasterServerRegister(LegacyRequest request)
    {
        var info = request.Body as JsonObject ?? new JsonObject();

        if (info["host"] is JsonObject nested)
        {
            info = nested;
        }

        var host = _hosts.Register(info, request.ClientIp);
        _logger.LogInformation("hosts: clientIp: {clientIp} host: {host}", request.ClientIp, host);
        _logger.LogInformation(
            "host registered {Name} {Ip}:{Port} map={Map} mode={Mode}",
            Json.ToText(host["name"]),
            Json.ToText(host["ip"]),
            Json.ToText(host["port"]),
            Json.ToText(host["mapIndex"]),
            Json.ToText(host["gameMode"]));

        return Reply.Ok();
    }

    public JsonNode MasterServerList(LegacyRequest request)
    {
        return HostList();
    }

    private JsonObject HostList()
    {
        var hosts = new JsonArray();

        foreach (var host in _hosts.Live())
        {
            hosts.Add(Json.CloneObject(host));
        }

        return new JsonObject { ["hosts"] = hosts, ["updated"] = true };
    }

    private static string RandomHash()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    private static readonly string[] ClientOwnedFields =
    {
        "settings", "suitNameIndex", "selectedSet", "nickname_color",
        "info0", "info1", "info2", "info3", "info4",
    };
}
