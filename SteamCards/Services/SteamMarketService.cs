using SteamCards.Models;
using System.Globalization;
using System.Text;
using System.Text.Json;
using static System.Net.WebRequestMethods;

namespace SteamCards.Services
{
	public class SteamMarketService
	{
		private readonly HttpClient _httpClient;
		public SteamMarketService(HttpClient httpClient)
		{
			_httpClient = httpClient;
		}
		public async Task<PriceCurrent?> GetLowestPriceAsync(string marketHashName, string currency = "18")
		{
			var encodedName = Uri.EscapeDataString(marketHashName);
			var url = $"https://steamcommunity.com/market/priceoverview/?appid=753&currency={currency}&country=UA&language=english&market_hash_name={encodedName}";

			var resp = await _httpClient.GetAsync(url);
			var body = await resp.Content.ReadAsStringAsync();

			Console.WriteLine($"[Steam] {resp.StatusCode} {url}");
			Console.WriteLine(body);

			var json = await _httpClient.GetStringAsync(url);
			
			using var doc = JsonDocument.Parse(json);
			if (!doc.RootElement.GetProperty("success").GetBoolean())
				return null;

			if (!doc.RootElement.TryGetProperty("lowest_price", out var lowestPriceElement))
				return null;

			var lowestRaw = lowestPriceElement.GetString();
			if (string.IsNullOrEmpty(lowestRaw))
				return null;

			var cleaned = new string(lowestRaw.Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray());
			cleaned = cleaned.Replace(',', '.');

			if (!decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var price))
				return null;

			return new PriceCurrent
			{
				MarketHashName = marketHashName,
				LowestPrice = price,
				Currency = currency,
				UpdatedAtUtc = DateTime.UtcNow
			};
		}
	}
}
