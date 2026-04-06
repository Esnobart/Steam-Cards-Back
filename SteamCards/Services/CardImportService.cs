using MongoDB.Driver;
using SteamCards.Models;
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

			for (int attempt = 1; attempt <= 3; attempt++)
			{
				using var resp = await _httpClient.GetAsync(url);

				if ((int)resp.StatusCode == 429)
				{
					Console.WriteLine($"Steam 429 on priceoverview, retry {attempt}, item={marketHashName}");
					await Task.Delay(TimeSpan.FromSeconds(8 * attempt));
					continue;
				}

				if (!resp.IsSuccessStatusCode)
					return null;

				var body = await resp.Content.ReadAsStringAsync();

				using var doc = JsonDocument.Parse(body);

				if (!doc.RootElement.TryGetProperty("lowest_price", out var PriceEl))
					return null;

				var priceText = PriceEl.GetString();

				if (string.IsNullOrEmpty(priceText))
					return null;

				priceText = priceText.Replace("₴", "").Trim();

				if (decimal.TryParse(priceText, out var price))
					return price;

				return null;
			}

			return null;
		}

		public async Task<ImportCardsResult> ImportForGameAsync(int appId)
		{
			await Task.Delay(TimeSpan.FromSeconds(5));
			var seen = new HashSet<string>(StringComparer.Ordinal);
			int normalImported = 0;
			int foilImported = 0;

			for (int start = 0; ;)
			{
				var url =
					"https://steamcommunity.com/market/search/render/?" +
					$"appid=753&currency=18&country=UA&norender=1&count=50&start={start}" +
					$"&category_753_Game%5B0%5D=tag_app_{appId}" +
					$"&category_753_item_class%5B0%5D=tag_item_class_2" +
					$"&q=&l=english";

				HttpResponseMessage? resp = null;

				for (int attempt = 1; attempt <= 5; attempt++)
				{
					resp?.Dispose();
					resp = await _httpClient.GetAsync(url);

					if ((int)resp.StatusCode == 429)
					{
						Console.WriteLine($"Steam 429 on search/render, retry {attempt}, appId={appId}, start={start}");
						await Task.Delay(TimeSpan.FromSeconds(10 * attempt));
						continue;
					}

					if ((int)resp.StatusCode == 403)
						throw new Exception("Steam forbidden 403");

					break;
				}

				if (resp is null)
					throw new Exception("Steam response was null");

				using (resp)
				{
					var body = await resp.Content.ReadAsStringAsync();

					if ((int)resp.StatusCode == 429)
						throw new Exception("Steam throttled 429 after retries");

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
						string? GameName = null;
						string? priceText = null;
						bool isFoil = false;

						if (item.TryGetProperty("asset_description", out var asset))
						{
							if (asset.TryGetProperty("market_hash_name", out var hash))
								marketHashName = hash.GetString();

							if (asset.TryGetProperty("type", out var type))
							{
								isFoil = type.GetString()?.Contains("Foil", StringComparison.OrdinalIgnoreCase) == true;
								GameName = Regex.Replace(type.GetString() ?? "", @"\s+(Foil\s+)?Trading Card$", "", RegexOptions.IgnoreCase).Trim();
							}
						}

						if (string.IsNullOrWhiteSpace(marketHashName) &&
							item.TryGetProperty("hash_name", out var hashName))
						{
							marketHashName = hashName.GetString();
						}

						if (string.IsNullOrWhiteSpace(marketHashName))
							continue;

						if (!seen.Add(marketHashName))
							continue;

						decimal? price = await GetPriceAsync(marketHashName);

						if (item.TryGetProperty("sell_price_text", out var currencyEl))
							priceText = currencyEl.GetString();

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
							GameName = GameName,
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

						if (isFoil)
							foilImported++;
						else
							normalImported++;

						await Task.Delay(Random.Shared.Next(7000, 12000));
					}

					start += pageSize;

					if (start >= totalCount)
						break;
				}

				await Task.Delay(Random.Shared.Next(12000, 18000));
			}

			return new ImportCardsResult
			{
				NormalImported = normalImported,
				FoilImported = foilImported
			};
		}
	}
}
