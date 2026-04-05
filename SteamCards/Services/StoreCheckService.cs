using System.Text.Json;

namespace SteamCards.Services
{
	public class StoreCheckService
	{
		private readonly HttpClient _httpClient;

		public StoreCheckService(HttpClient httpClient)
		{
			_httpClient = httpClient;
		}

		public async Task<bool> CheckAppIdExistsAsync(int appId, CancellationToken ct = default)
		{
			var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=categories";

			using var resp = await _httpClient.GetAsync(url, ct);
			var body = await resp.Content.ReadAsStringAsync(ct);

			if ((int)resp.StatusCode == 429)
			{
				Console.WriteLine($"[THROTTLED] {(int)resp.StatusCode} {url}");
				throw new Exception($"Store API throttled");
			}

			if (!resp.IsSuccessStatusCode)
			{
				Console.WriteLine($"[THROTTLED] {(int)resp.StatusCode} {url}");
				return false;
			}

			using var doc = JsonDocument.Parse(body);

			if (!doc.RootElement.TryGetProperty(appId.ToString(), out var appElement))
			{
				Console.WriteLine($"[ERROR] Missing appid {appId} in response");
				return false;
			}
			if (!appElement.TryGetProperty("success", out var successEl) || !successEl.GetBoolean())
			{
				Console.WriteLine($"[INFO] AppId {appId} does not exist");
				return false;
			}
			if (!appElement.TryGetProperty("data", out var dataEl) || dataEl.ValueKind == JsonValueKind.Null)
			{
				Console.WriteLine($"[ERROR] Missing categories for AppId {appId}");
				return false;
			}
			if (!dataEl.TryGetProperty("categories", out var categories) || categories.ValueKind != JsonValueKind.Array)
			{
				Console.WriteLine($"[INFO] AppId {appId} has no categories");
				return false;
			}

			foreach (var category in categories.EnumerateArray())
			{
				if (category.TryGetProperty("id", out var idEl) && idEl.GetInt32() == 29)
				{
					Console.WriteLine($"[INFO] AppId {appId} has traiding cards");
					return true;
				}
					
			}

			Console.WriteLine($"[INFO] AppId {appId} does not have trading cards");
			return false;
		}
	}
}
