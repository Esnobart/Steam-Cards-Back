using MongoDB.Driver;
using SteamCards.Models;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamCards.Services
{
	public class CardImportService
	{
		private readonly HttpClient _httpClient;
		private readonly IMongoCollection<Cards> _cards;

		public CardImportService(HttpClient httpClient, IMongoDatabase database)
		{
			_httpClient = httpClient;
			_cards = database.GetCollection<Cards>("cards");
		}

		private async Task<decimal?> GetPriceAsync(string marketHashName)
		{
			var url =
				$"https://steamcommunity.com/market/priceoverview/?appid=753&currency=18&country=UA&language=english&market_hash_name={Uri.EscapeDataString(marketHashName)}";

			using var resp = await _httpClient.GetAsync(url);

			if (!resp.IsSuccessStatusCode)
				return null;

			var body = await resp.Content.ReadAsStringAsync();
			
			using var doc = JsonDocument.Parse(body);

			if (!doc.RootElement.TryGetProperty("lowest_price", out var priceEl))
				return null;

			var priceText = priceEl.GetString();

			if (string.IsNullOrWhiteSpace(priceText))
				return null;

			var cleanPrice = new string(priceText.Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray());
			cleanPrice = cleanPrice.Replace(',', '.');

			if (decimal.TryParse(cleanPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var price))
				return price;

			
			return null;
		}

		public async Task<ImportCardsResult> ImportForGameAsync(int appId)
		{
			var seen = new HashSet<string>(StringComparer.Ordinal);

			var normalImported = await ImportCardsBorderAsync(appId, false, seen);
			var foilImported = await ImportCardsBorderAsync(appId, true, seen);

			return new ImportCardsResult
			{
				NormalImported = normalImported,
				FoilImported = foilImported,
			}
		};


		private async Task<int> ImportCardsBorderAsync(int appId, bool isFoilExpected, HashSet<string> seen)
		{
			var imported = 0;
			var cardBorder = isFoilExpected ? 1 : 0;

			for (int start = 0; ;)
			{
				var url =
					"https://steamcommunity.com/market/search/render/?" +
					$"appid=753&currency=18&country=UA&norender=1&count=50&start={start}" +
					$"&category_753_Game%5B0%5D=tag_app_{appId}" +
					$"&category_753_item_class%5B0%5D=tag_item_class_2" +
					$"&category_753_cardborder%5B0%5D=tag_cardborder_{cardBorder}" +
					$"&q=&l=english";

				using var resp = await _httpClient.GetAsync(url);

				var body = await resp.Content.ReadAsStringAsync();

				if ((int)resp.StatusCode is 429 or 403)
					throw new Exception($"Steam throttled {(int)resp.StatusCode}");

				if (!resp.IsSuccessStatusCode)
					throw new Exception($"Steam HTTP {(int)resp.StatusCode}");

				using var doc = JsonDocument.Parse(body);
				var root = doc.RootElement;

				if (!root.TryGetProperty("success", out var successEl) || !successEl.GetBoolean())
					break;

				int totalCount = root.GetProperty("total_count").GetInt32();
				int pageSize = root.GetProperty("pagesize").GetInt32();

				if (!root.TryGetProperty("results", out var resultsEl) || resultsEl.ValueKind != JsonValueKind.Array)
					break;

				foreach (var item in resultsEl.EnumerateArray())
				{
					string? marketHashName = null;
					string? cardName = null;
					string? gameName = null;
					string? priceText = null;
					bool isFoil = isFoilExpected;

					if (item.TryGetProperty("asset_description", out var asset))
					{
						if (asset.TryGetProperty("market_hash_name", out var hash))
							marketHashName = hash.GetString();

						if (asset.TryGetProperty("type", out var type))
						{
							isFoil = type.GetString()?.Contains("Foil", StringComparison.OrdinalIgnoreCase) == true;
							gameName = Regex.Replace(type.GetString() ?? "", @"\s+(Foil\s+)?Trading Card$", "", RegexOptions.IgnoreCase).Trim();
						}
					}

					decimal? price = await GetPriceAsync(marketHashName);

					if (item.TryGetProperty("sell_price_text", out var currencyEl))
						priceText = currencyEl.GetString();

					if (string.IsNullOrWhiteSpace(marketHashName) &&
						item.TryGetProperty("hash_name", out var hashName))
					{
						marketHashName = hashName.GetString();
					}

					if (string.IsNullOrWhiteSpace(marketHashName))
						continue;

					if (!seen.Add(marketHashName))
						continue;

					if (item.TryGetProperty("name", out var name))
						cardName = name.GetString();

					cardName ??= marketHashName;

					var prefix = $"{appId}-";
					if (cardName.StartsWith(prefix))
						cardName = cardName[prefix.Length..];

					cardName = cardName
						.Replace(" (Trading Card)", "", StringComparison.OrdinalIgnoreCase)
						.Replace(" (Foil Trading Card)", "", StringComparison.OrdinalIgnoreCase)
						.Trim();

					var card = new Cards
					{
						AppId = appId,
						GameName = gameName,
						MarketHashName = marketHashName,
						CardName = cardName,
						Price = price,
						PriceText = priceText,
						IsFoil = isFoil,
						CreatedAtUtc = DateTime.UtcNow
					};

					await _cards.ReplaceOneAsync(
						c => c.MarketHashName == marketHashName,
						card,
						new ReplaceOptions { IsUpsert = true }
					);

					imported++;

					await Task.Delay(Random.Shared.Next(1000, 1500));
				}

				start += pageSize;

				if (start >= totalCount)
					break;

				await Task.Delay(Random.Shared.Next(1500, 2000));
			}

			return imported;
		}
	}
}
