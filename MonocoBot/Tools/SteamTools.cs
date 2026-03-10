using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MonocoBot.Configuration;

namespace MonocoBot.Tools;

public class SteamTools
{
    private readonly HttpClient _httpClient;
    private readonly string _itadApiKey;

    private static readonly Dictionary<string, string> CurrencySymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ZAR"] = "R", ["USD"] = "$", ["EUR"] = "€", ["GBP"] = "£",
        ["CAD"] = "CA$", ["AUD"] = "A$", ["BRL"] = "R$", ["JPY"] = "¥",
        ["CNY"] = "¥", ["INR"] = "₹", ["PLN"] = "zł", ["TRY"] = "₺",
    };

    private static readonly Dictionary<string, string> CurrencyToSteamCc = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ZAR"] = "za", ["USD"] = "us", ["EUR"] = "de", ["GBP"] = "gb",
        ["CAD"] = "ca", ["AUD"] = "au", ["BRL"] = "br", ["JPY"] = "jp",
        ["CNY"] = "cn", ["INR"] = "in", ["PLN"] = "pl", ["TRY"] = "tr",
    };

    public SteamTools(HttpClient httpClient, IOptions<BotOptions> options)
    {
        _httpClient = httpClient;
        _itadApiKey = options.Value.IsThereAnyDealApiKey;
    }

    private static string FormatPrice(double amount, string currency)
    {
        var symbol = CurrencySymbols.GetValueOrDefault(currency, currency + " ");
        return $"{symbol}{amount:F2}";
    }

    private async Task<double?> GetExchangeRateAsync(string fromCurrency, string toCurrency)
    {
        if (fromCurrency.Equals(toCurrency, StringComparison.OrdinalIgnoreCase))
            return 1.0;

        try
        {
            var url = $"https://open.er-api.com/v6/latest/{fromCurrency.ToUpperInvariant()}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "MonocoBot/1.0");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("rates", out var rates) &&
                rates.TryGetProperty(toCurrency.ToUpperInvariant(), out var rate))
            {
                return rate.GetDouble();
            }
        }
        catch { }

        return null;
    }

    [Description("Extracts a numeric 64-bit Steam ID from a Steam profile URL. " +
        "Accepts a full profile URL (e.g. 'steamcommunity.com/profiles/76561198012345678') or a raw 64-bit ID. " +
        "Note: vanity name resolution (e.g. 'steamcommunity.com/id/gaben') is not supported — " +
        "ask the user for their full profile URL with the numeric ID instead.")]
    public string ResolveSteamId([Description("A Steam profile URL or 64-bit Steam ID")] string profileInput)
    {
        var input = profileInput.Trim().TrimEnd('/');

        if (input.Contains("steamcommunity.com", StringComparison.OrdinalIgnoreCase))
        {
            if (!input.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                input = "https://" + input;

            try
            {
                var uri = new Uri(input);
                var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (segments is ["profiles", var id, ..] && id.Length >= 15 && id.All(char.IsDigit))
                    return $"Steam ID: {id}";

                if (segments is ["id", ..])
                    return "This is a vanity URL. Ask the user for their Steam profile URL that contains a numeric ID " +
                           "(steamcommunity.com/profiles/NUMBERS), or check the local profiles file with GetLocalProfileData.";
            }
            catch
            {
                return "Could not parse the Steam profile URL.";
            }
        }

        if (input.Length >= 15 && input.All(char.IsDigit))
            return $"Steam ID: {input}";

        return "Could not extract a Steam ID. Ask the user for their Steam profile URL " +
               "(steamcommunity.com/profiles/NUMBERS), or try GetLocalProfileData to check the local profiles file.";
    }

    [Description("Gets a Steam user's public wishlist. " +
        "Only works if the wishlist is set to Public in Steam privacy settings. " +
        "If not accessible, try GetLocalProfileData to check the local profiles file instead.")]
    public async Task<string> GetWishlist([Description("The Steam 64-bit ID")] string steamId)
    {
        try
        {
            var items = new List<(uint AppId, string Name)>();
            var page = 0;

            while (true)
            {
                var url = $"https://store.steampowered.com/wishlist/profiles/{steamId}/wishlistdata/?p={page}";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("User-Agent", "Mozilla/5.0");

                var response = await _httpClient.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();

                if (string.IsNullOrWhiteSpace(json) || json is "[]" or "null")
                    break;

                var doc = JsonDocument.Parse(json);
                var pageItems = 0;

                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    if (uint.TryParse(entry.Name, out var appId))
                    {
                        var name = entry.Value.TryGetProperty("name", out var n)
                            ? n.GetString() ?? $"App {appId}"
                            : $"App {appId}";
                        items.Add((appId, name));
                        pageItems++;
                    }
                }

                if (pageItems == 0)
                    break;

                page++;
            }

            if (items.Count == 0)
                return "Could not access this user's wishlist. It may be set to private or the profile doesn't exist. " +
                       "Try GetLocalProfileData to check the local profiles file instead.";

            var names = items
                .Select(i => i.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return $"**Steam Wishlist** ({names.Count} items):\n{string.Join("\n", names.Select(n => $"- {n}"))}";
        }
        catch (Exception ex)
        {
            return $"Failed to get wishlist: {ex.Message}";
        }
    }

    [Description("Looks up game, wishlist, and recently played data from the local steam_profiles.json file. " +
        "Use this as a fallback when Steam data can't be fetched (e.g. private profile, no Steam ID available). " +
        "Returns whatever data is available for the given profile name.")]
    public string GetLocalProfileData([Description("The profile name/key as configured in steam_profiles.json (e.g. a Discord username or nickname)")] string profileName)
    {
        try
        {
            var profilesPath = Path.Combine(AppContext.BaseDirectory, "steam_profiles.json");
            if (!File.Exists(profilesPath))
                return "No steam_profiles.json file found.";

            var json = File.ReadAllText(profilesPath);
            var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("profiles", out var profiles))
                return "No profiles configured in steam_profiles.json.";

            foreach (var profile in profiles.EnumerateObject())
            {
                if (!string.Equals(profile.Name, profileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var displayName = profile.Value.TryGetProperty("displayName", out var dn)
                    ? dn.GetString() : profile.Name;

                var result = $"**Profile:** {displayName}\n";

                if (profile.Value.TryGetProperty("games", out var games))
                {
                    var gameList = games.EnumerateArray()
                        .Select(g => $"- {g.GetString()}")
                        .OrderBy(g => g)
                        .ToList();
                    result += $"\n**Games ({gameList.Count}):**\n{string.Join("\n", gameList)}";
                }

                if (profile.Value.TryGetProperty("wishlist", out var wishlist))
                {
                    var wishlistItems = wishlist.EnumerateArray()
                        .Select(g => $"- {g.GetString()}")
                        .OrderBy(g => g)
                        .ToList();
                    result += $"\n\n**Wishlist ({wishlistItems.Count}):**\n{string.Join("\n", wishlistItems)}";
                }

                if (profile.Value.TryGetProperty("recentlyPlayed", out var recent))
                {
                    var recentItems = recent.EnumerateArray()
                        .Select(g => $"- {g.GetString()}")
                        .ToList();
                    result += $"\n\n**Recently Played ({recentItems.Count}):**\n{string.Join("\n", recentItems)}";
                }

                return result;
            }

            var available = string.Join(", ", profiles.EnumerateObject().Select(p => p.Name));
            return available.Length > 0
                ? $"Profile '{profileName}' not found in steam_profiles.json. Available profiles: {available}"
                : $"Profile '{profileName}' not found and no profiles are configured in steam_profiles.json.";
        }
        catch (Exception ex)
        {
            return $"Failed to read local profile data: {ex.Message}";
        }
    }

    [Description("Looks up pricing, deals, and sale information for a specific game by searching across multiple stores using IsThereAnyDeal. " +
        "Use this to find current prices, discounts, or where a game is cheapest. " +
        "Prices default to South African Rand (ZAR). Specify a different currency code if needed.")]
    public async Task<string> LookupGameDeals([Description("The name of the game to look up deals for")] string gameName,
        [Description("Currency code for prices (default: ZAR). Examples: USD, EUR, GBP, CAD, AUD")] string currency = "ZAR")
    {
        if (string.IsNullOrWhiteSpace(_itadApiKey))
            return "IsThereAnyDeal API key is not configured. Add it to the Bot:IsThereAnyDealApiKey setting.";

        try
        {
            currency = currency.ToUpperInvariant();

            var encoded = System.Net.WebUtility.UrlEncode(gameName);
            var searchUrl = $"https://api.isthereanydeal.com/games/search/v1?title={encoded}&results=5&key={_itadApiKey}";

            var searchRequest = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            searchRequest.Headers.Add("User-Agent", "MonocoBot/1.0");
            var searchResponse = await _httpClient.SendAsync(searchRequest);
            searchResponse.EnsureSuccessStatusCode();
            var searchJson = await searchResponse.Content.ReadAsStringAsync();

            var searchResults = JsonDocument.Parse(searchJson).RootElement;

            if (searchResults.GetArrayLength() == 0)
                return $"No games found matching '{gameName}' on IsThereAnyDeal.";

            var gameIds = new List<string>();
            var titleMap = new Dictionary<string, string>();
            var slugMap = new Dictionary<string, string>();

            foreach (var game in searchResults.EnumerateArray())
            {
                var id = game.GetProperty("id").GetString()!;
                var title = game.GetProperty("title").GetString() ?? "Unknown";
                var slug = game.TryGetProperty("slug", out var s) ? s.GetString() ?? "" : "";
                gameIds.Add(id);
                titleMap[id] = title;
                slugMap[id] = slug;
            }

            var pricesUrl = $"https://api.isthereanydeal.com/games/prices/v2?key={_itadApiKey}&country=US&capacity=3";
            var pricesRequest = new HttpRequestMessage(HttpMethod.Post, pricesUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(gameIds), Encoding.UTF8, "application/json")
            };
            pricesRequest.Headers.Add("User-Agent", "MonocoBot/1.0");
            var pricesResponse = await _httpClient.SendAsync(pricesRequest);
            pricesResponse.EnsureSuccessStatusCode();
            var pricesJson = await pricesResponse.Content.ReadAsStringAsync();

            double exchangeRate;
            string displayCurrency;
            if (currency == "USD")
            {
                exchangeRate = 1.0;
                displayCurrency = "USD";
            }
            else
            {
                var rate = await GetExchangeRateAsync("USD", currency);
                if (rate is not null)
                {
                    exchangeRate = rate.Value;
                    displayCurrency = currency;
                }
                else
                {
                    exchangeRate = 1.0;
                    displayCurrency = "USD";
                }
            }

            var pricesResults = JsonDocument.Parse(pricesJson).RootElement;
            var lines = new List<string>();

            foreach (var entry in pricesResults.EnumerateArray())
            {
                var id = entry.GetProperty("id").GetString()!;
                var title = titleMap.GetValueOrDefault(id, "Unknown");
                var slug = slugMap.GetValueOrDefault(id, "");

                if (!entry.TryGetProperty("deals", out var deals) || deals.GetArrayLength() == 0)
                {
                    lines.Add($"- **{title}** — no current deals");
                    continue;
                }

                var dealLines = new List<string>();
                foreach (var deal in deals.EnumerateArray())
                {
                    var shopName = deal.GetProperty("shop").GetProperty("name").GetString() ?? "Unknown";
                    var price = deal.GetProperty("price").GetProperty("amount").GetDouble() * exchangeRate;
                    var regular = deal.GetProperty("regular").GetProperty("amount").GetDouble() * exchangeRate;
                    var cut = deal.GetProperty("cut").GetInt32();
                    var dealUrl = deal.TryGetProperty("url", out var u) ? u.GetString() : null;

                    var dealText = cut > 0
                        ? $"  - {shopName}: **{FormatPrice(price, displayCurrency)}** (~~{FormatPrice(regular, displayCurrency)}~~ -{cut}%)"
                        : $"  - {shopName}: **{FormatPrice(price, displayCurrency)}**";

                    if (dealUrl is not null)
                        dealText += $" — [buy]({dealUrl})";

                    dealLines.Add(dealText);
                }

                var itadLink = !string.IsNullOrEmpty(slug)
                    ? $" ([view on ITAD](https://isthereanydeal.com/game/{slug}/info/))"
                    : "";

                lines.Add($"- **{title}**{itadLink}\n{string.Join("\n", dealLines)}");
            }

            return lines.Count > 0
                ? $"**Deals for \"{gameName}\" ({displayCurrency}):**\n{string.Join("\n", lines)}"
                : $"No deals found for '{gameName}'.";
        }
        catch (Exception ex)
        {
            return $"Failed to look up game deals: {ex.Message}";
        }
    }

    [Description("Looks up the current price of a game directly on the Steam store. " +
        "Prices default to South African Rand (ZAR). Specify a different currency code if needed (e.g. USD, EUR, GBP).")]
    public async Task<string> LookupSteamPrice(
        [Description("The name of the game to look up on Steam")] string gameName,
        [Description("Currency code for prices (default: ZAR). Examples: USD, EUR, GBP, CAD, AUD")] string currency = "ZAR")
    {
        try
        {
            currency = currency.ToUpperInvariant();
            var cc = CurrencyToSteamCc.GetValueOrDefault(currency, "za");

            var encoded = System.Net.WebUtility.UrlEncode(gameName);
            var searchUrl = $"https://store.steampowered.com/api/storesearch/?term={encoded}&l=english&cc={cc}";

            var searchRequest = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            searchRequest.Headers.Add("User-Agent", "MonocoBot/1.0");
            var searchResponse = await _httpClient.SendAsync(searchRequest);
            searchResponse.EnsureSuccessStatusCode();
            var searchJson = await searchResponse.Content.ReadAsStringAsync();

            var searchDoc = JsonDocument.Parse(searchJson);
            var total = searchDoc.RootElement.GetProperty("total").GetInt32();
            if (total == 0)
                return $"No games found matching '{gameName}' on Steam.";

            var items = searchDoc.RootElement.GetProperty("items");
            var lines = new List<string>();

            foreach (var item in items.EnumerateArray().Take(5))
            {
                var appId = item.GetProperty("id").GetInt32();
                var name = item.GetProperty("name").GetString() ?? "Unknown";

                var detailUrl = $"https://store.steampowered.com/api/appdetails?appids={appId}&cc={cc}";
                var detailRequest = new HttpRequestMessage(HttpMethod.Get, detailUrl);
                detailRequest.Headers.Add("User-Agent", "MonocoBot/1.0");
                var detailResponse = await _httpClient.SendAsync(detailRequest);
                var detailJson = await detailResponse.Content.ReadAsStringAsync();

                var detailDoc = JsonDocument.Parse(detailJson);
                var appData = detailDoc.RootElement.GetProperty(appId.ToString());
                var storeUrl = $"https://store.steampowered.com/app/{appId}";

                if (!appData.GetProperty("success").GetBoolean())
                {
                    lines.Add($"- **{name}** — details unavailable ([Steam]({storeUrl}))");
                    continue;
                }

                var data = appData.GetProperty("data");

                if (data.TryGetProperty("is_free", out var isFree) && isFree.GetBoolean())
                {
                    lines.Add($"- **{name}** — **Free to Play** ([Steam]({storeUrl}))");
                    continue;
                }

                if (!data.TryGetProperty("price_overview", out var priceOverview))
                {
                    lines.Add($"- **{name}** — price unavailable ([Steam]({storeUrl}))");
                    continue;
                }

                var priceCurrency = priceOverview.GetProperty("currency").GetString() ?? currency;
                var finalPrice = priceOverview.GetProperty("final").GetInt32() / 100.0;
                var initialPrice = priceOverview.GetProperty("initial").GetInt32() / 100.0;
                var discountPercent = priceOverview.GetProperty("discount_percent").GetInt32();

                if (!priceCurrency.Equals(currency, StringComparison.OrdinalIgnoreCase))
                {
                    var rate = await GetExchangeRateAsync(priceCurrency, currency);
                    if (rate is not null)
                    {
                        finalPrice *= rate.Value;
                        initialPrice *= rate.Value;
                        priceCurrency = currency;
                    }
                }

                var priceText = discountPercent > 0
                    ? $"**{FormatPrice(finalPrice, priceCurrency)}** (~~{FormatPrice(initialPrice, priceCurrency)}~~ -{discountPercent}%)"
                    : $"**{FormatPrice(finalPrice, priceCurrency)}**";

                lines.Add($"- **{name}** — {priceText} ([Steam]({storeUrl}))");
            }

            return $"**Steam prices for \"{gameName}\" ({currency}):**\n{string.Join("\n", lines)}";
        }
        catch (Exception ex)
        {
            return $"Failed to look up Steam price: {ex.Message}";
        }
    }
}
