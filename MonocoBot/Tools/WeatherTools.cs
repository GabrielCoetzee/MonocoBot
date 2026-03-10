using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace MonocoBot.Tools;

public class WeatherTools
{
    private readonly HttpClient _httpClient;

    private static readonly Dictionary<int, string> WeatherDescriptions = new()
    {
        [0] = "Clear sky",
        [1] = "Mainly clear", [2] = "Partly cloudy", [3] = "Overcast",
        [45] = "Fog", [48] = "Depositing rime fog",
        [51] = "Light drizzle", [53] = "Moderate drizzle", [55] = "Dense drizzle",
        [56] = "Light freezing drizzle", [57] = "Dense freezing drizzle",
        [61] = "Slight rain", [63] = "Moderate rain", [65] = "Heavy rain",
        [66] = "Light freezing rain", [67] = "Heavy freezing rain",
        [71] = "Slight snow fall", [73] = "Moderate snow fall", [75] = "Heavy snow fall",
        [77] = "Snow grains",
        [80] = "Slight rain showers", [81] = "Moderate rain showers", [82] = "Violent rain showers",
        [85] = "Slight snow showers", [86] = "Heavy snow showers",
        [95] = "Thunderstorm", [96] = "Thunderstorm with slight hail", [99] = "Thunderstorm with heavy hail",
    };

    public WeatherTools(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    [Description("Gets the current weather conditions for a location. " +
        "Returns temperature, humidity, wind, and conditions.")]
    public async Task<string> GetCurrentWeather(
        [Description("The city or location name (e.g. 'Cape Town', 'London', 'New York')")] string location)
    {
        try
        {
            var (lat, lon, name, country) = await GeocodeLocationAsync(location);
            if (name is null)
                return $"Could not find location '{location}'. Try a different city name.";

            var latStr = lat.ToString(CultureInfo.InvariantCulture);
            var lonStr = lon.ToString(CultureInfo.InvariantCulture);
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={latStr}&longitude={lonStr}" +
                      "&current=temperature_2m,relative_humidity_2m,apparent_temperature,precipitation,weather_code,wind_speed_10m,wind_gusts_10m" +
                      "&timezone=auto";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "MonocoBot/1.0");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            var doc = JsonDocument.Parse(json);
            var current = doc.RootElement.GetProperty("current");

            var temp = current.GetProperty("temperature_2m").GetDouble();
            var feelsLike = current.GetProperty("apparent_temperature").GetDouble();
            var humidity = current.GetProperty("relative_humidity_2m").GetInt32();
            var precip = current.GetProperty("precipitation").GetDouble();
            var windSpeed = current.GetProperty("wind_speed_10m").GetDouble();
            var windGusts = current.GetProperty("wind_gusts_10m").GetDouble();
            var weatherCode = current.GetProperty("weather_code").GetInt32();
            var condition = WeatherDescriptions.GetValueOrDefault(weatherCode, "Unknown");
            var timezone = doc.RootElement.GetProperty("timezone").GetString() ?? "UTC";

            return $"""
                **Current weather in {name}, {country}:**
                - **Conditions:** {condition}
                - **Temperature:** {temp}°C (feels like {feelsLike}°C)
                - **Humidity:** {humidity}%
                - **Precipitation:** {precip} mm
                - **Wind:** {windSpeed} km/h (gusts {windGusts} km/h)
                - **Timezone:** {timezone}
                """;
        }
        catch (Exception ex)
        {
            return $"Failed to get weather: {ex.Message}";
        }
    }

    [Description("Gets a multi-day weather forecast for a location. " +
        "Returns daily high/low temperatures, conditions, precipitation chance, and wind.")]
    public async Task<string> GetWeatherForecast(
        [Description("The city or location name (e.g. 'Cape Town', 'London', 'New York')")] string location,
        [Description("Number of forecast days (1-7, default: 3)")] int days = 3)
    {
        try
        {
            days = Math.Clamp(days, 1, 7);

            var (lat, lon, name, country) = await GeocodeLocationAsync(location);
            if (name is null)
                return $"Could not find location '{location}'. Try a different city name.";

            var latStr = lat.ToString(CultureInfo.InvariantCulture);
            var lonStr = lon.ToString(CultureInfo.InvariantCulture);
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={latStr}&longitude={lonStr}" +
                      "&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min,precipitation_sum,precipitation_probability_max,wind_speed_10m_max" +
                      $"&timezone=auto&forecast_days={days}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "MonocoBot/1.0");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            var doc = JsonDocument.Parse(json);
            var daily = doc.RootElement.GetProperty("daily");

            var dates = daily.GetProperty("time");
            var codes = daily.GetProperty("weather_code");
            var maxTemps = daily.GetProperty("temperature_2m_max");
            var minTemps = daily.GetProperty("temperature_2m_min");
            var feelsMax = daily.GetProperty("apparent_temperature_max");
            var feelsMin = daily.GetProperty("apparent_temperature_min");
            var precipSum = daily.GetProperty("precipitation_sum");
            var precipProb = daily.GetProperty("precipitation_probability_max");
            var windMax = daily.GetProperty("wind_speed_10m_max");

            var lines = new List<string>();
            for (var i = 0; i < dates.GetArrayLength() && i < days; i++)
            {
                var date = dates[i].GetString() ?? "?";
                var code = codes[i].GetInt32();
                var condition = WeatherDescriptions.GetValueOrDefault(code, "Unknown");
                var high = maxTemps[i].GetDouble();
                var low = minTemps[i].GetDouble();
                var flHigh = feelsMax[i].GetDouble();
                var flLow = feelsMin[i].GetDouble();
                var rain = precipSum[i].GetDouble();
                var rainChance = precipProb[i].GetInt32();
                var wind = windMax[i].GetDouble();

                lines.Add($"**{date}** — {condition}\n" +
                    $"  High: {high}°C (feels {flHigh}°C) · Low: {low}°C (feels {flLow}°C)\n" +
                    $"  Precip: {rain} mm ({rainChance}% chance) · Wind: up to {wind} km/h");
            }

            return $"**{days}-day forecast for {name}, {country}:**\n{string.Join("\n", lines)}";
        }
        catch (Exception ex)
        {
            return $"Failed to get forecast: {ex.Message}";
        }
    }

    private async Task<(double Lat, double Lon, string? Name, string? Country)> GeocodeLocationAsync(string location)
    {
        var encoded = WebUtility.UrlEncode(location);
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={encoded}&count=1&language=en";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "MonocoBot/1.0");
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();

        var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            return (0, 0, null, null);

        var first = results[0];
        var lat = first.GetProperty("latitude").GetDouble();
        var lon = first.GetProperty("longitude").GetDouble();
        var name = first.TryGetProperty("name", out var n) ? n.GetString() : location;
        var country = first.TryGetProperty("country", out var c) ? c.GetString() : "";

        return (lat, lon, name, country);
    }
}
