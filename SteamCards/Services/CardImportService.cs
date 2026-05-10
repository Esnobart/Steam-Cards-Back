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

		private static decimal? ParsePrice(string? priceText)
		{
			if (string.IsNullOrWhiteSpace(priceText))
				return null;

			var cleanPrice = new string(priceText.Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray());
			cleanPrice = cleanPrice.Replace(',', '.');

			if (decimal.TryParse(cleanPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var price))
				return price;

			return null;
		}

		private async Task<decimal?> GetPriceAsync(string marketHashName, CancellationToken cancellationToken = default)
		{
			var url =
				$"https://steamcommunity.com/market/priceoverview/?appid=753&currency=18&country=UA&language=english&market_hash_name={Uri.EscapeDataString(marketHashName)}";

			using var resp = await _httpClient.GetAsync(url, cancellationToken);

			if (!resp.IsSuccessStatusCode)
				return null;

			var body = await resp.Content.ReadAsStringAsync(cancellationToken);
			
			using var doc = JsonDocument.Parse(body);

			if (!doc.RootElement.TryGetProperty("lowest_price", out var priceEl))
				return null;

			return ParsePrice(priceEl.GetString());
		}

		public async Task<ImportCardsResult> ImportForGameAsync(
			int appId,
			bool? isFoilFilter = null,
			CancellationToken cancellationToken = default)
		{
			return await ImportCardsAsync(appId, isFoilFilter, cancellationToken);
		}


		private async Task<ImportCardsResult> ImportCardsAsync(
			int appId,
			bool? isFoilFilter,
			CancellationToken cancellationToken)
		{
			var result = new ImportCardsResult();
			var seen = new HashSet<string>(StringComparer.Ordinal);

			for (int start = 0; ;)
			{
				var cardBorderFilter = isFoilFilter.HasValue
					? $"&category_753_cardborder%5B0%5D=tag_cardborder_{(isFoilFilter.Value ? 1 : 0)}"
					: string.Empty;

				var url =
					"https://steamcommunity.com/market/search/render/?" +
					$"appid=753&currency=18&country=UA&norender=1&count=50&start={start}" +
					$"&category_753_Game%5B0%5D=tag_app_{appId}" +
					$"&category_753_item_class%5B0%5D=tag_item_class_2" +
					cardBorderFilter +
					$"&q=&l=english";

				using var resp = await _httpClient.GetAsync(url, cancellationToken);

				var body = await resp.Content.ReadAsStringAsync(cancellationToken);

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
					bool isFoil = isFoilFilter ?? false;

					if (item.TryGetProperty("asset_description", out var asset))
					{
						if (asset.TryGetProperty("market_hash_name", out var hash))
							marketHashName = hash.GetString();

						if (asset.TryGetProperty("type", out var type))
						{
							var typeText = type.GetString() ?? "";
							if (!isFoilFilter.HasValue)
								isFoil = typeText.Contains("Foil", StringComparison.OrdinalIgnoreCase);

							gameName = Regex.Replace(typeText, @"\s+(Foil\s+)?Trading Card$", "", RegexOptions.IgnoreCase).Trim();
						}
					}

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

					decimal? price = await GetPriceAsync(marketHashName, cancellationToken);

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
						GameName = gameName ?? string.Empty,
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
						new ReplaceOptions { IsUpsert = true },
						cancellationToken
					);

					if (isFoil)
						result.FoilImported++;
					else
						result.NormalImported++;

					await Task.Delay(Random.Shared.Next(1000, 1500), cancellationToken);
				}

				start += pageSize;

				if (start >= totalCount)
					break;

				await Task.Delay(Random.Shared.Next(1500, 2000), cancellationToken);
			}

			return result;
		}
	}
}
