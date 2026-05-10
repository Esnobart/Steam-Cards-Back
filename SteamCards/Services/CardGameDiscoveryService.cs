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

		public async Task<List<int>> DiscoveryService(CancellationToken ct = default)
		{
			var appIds = new HashSet<int>();
			
			for (var start = 0; ; start += 100)
			{
				var url =
					"https://store.steampowered.com/search/results/?" +
					$"category1=998&category2=29&start={start}&count=100&infinite=1&ignore_preferences=1";

				var json = await _httpClient.GetStringAsync(url, ct);

				using var doc = JsonDocument.Parse(json);
				var root = doc.RootElement;

				var total = root.GetProperty("total_count").GetInt32();
				var html = root.GetProperty("results_html").GetString() ?? "";

				foreach (Match match in Regex.Matches(html, "data-ds-appid=\"(?<id>\\d+)\""))
					appIds.Add(int.Parse(match.Groups["id"].Value));

				if (start + 100 > total) 
					break;

				await Task.Delay(2000, ct);
			}

			return appIds.ToList();
		}
	}
}
