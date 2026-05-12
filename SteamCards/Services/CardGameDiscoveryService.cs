using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamCards.Services
{
	public class CardGameDiscoveryService
	{
		private readonly HttpClient _httpClient;

		public CardGameDiscoveryService(HttpClient httpClient)
		{
			_httpClient = httpClient;
		}

		public class CardGameDiscoveryResult
		{
			public int SteamTotal { get; set; }
			public int Discovered => AppIds.Count;
			public int Pages { get; set; }
			public List<int> AppIds { get; set; } = new();
		}


		public async Task<CardGameDiscoveryResult> DiscoveryService(CancellationToken ct = default)
		{
			var appIds = new HashSet<int>();
			var steamTotal = 0;
			var pages = 0;

			for (var start = 0; ; start += 100)
			{
				var url =
					"https://store.steampowered.com/search/results/?" +
					$"category1=998&category2=29&start={start}&count=100&infinite=1&ignore_preferences=1";

				var resp = await _httpClient.GetAsync(url, ct);

				if ((int)resp.StatusCode == 429)
				{
					await Task.Delay(TimeSpan.FromSeconds(30), ct);
					start -= 100;
					continue;
				}

				resp.EnsureSuccessStatusCode();

				var json = await resp.Content.ReadAsStringAsync();

				using var doc = JsonDocument.Parse(json);
				var root = doc.RootElement;

				var total = root.GetProperty("total_count").GetInt32();
				var html = root.GetProperty("results_html").GetString() ?? "";

				foreach (Match match in Regex.Matches(html, "data-ds-appid=\"(?<ids>\\d+)\""))
				{
					foreach (var rawId in match.Groups["ids"].Value.Split(','))
					{
						if (int.TryParse(rawId, out var id))
							appIds.Add(id);
					}
				}

				steamTotal = total;
				pages++;

				if (start + 100 > total)
					break;

				await Task.Delay(5000, ct);
			}

			return new CardGameDiscoveryResult
			{
				SteamTotal = steamTotal,
				Pages = pages,
				AppIds = appIds.ToList()
			};
		}
	}
}