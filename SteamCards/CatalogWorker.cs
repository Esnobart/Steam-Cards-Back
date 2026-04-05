using MongoDB.Driver;
using SteamCards.Services;
using SteamCards.Models;

namespace SteamCards
{
	public class CatalogWorker : BackgroundService
	{
		private readonly IServiceProvider _sp;

		private static readonly TimeSpan NoCardsRecheckAfter = TimeSpan.FromDays(30);
		private static readonly TimeSpan ThrottleRetryAfter = TimeSpan.FromMinutes(30);
		private static readonly TimeSpan MarketThrottleRetryAfter = TimeSpan.FromHours(2);
		private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(10);

		public CatalogWorker(IServiceProvider sp)
		{
			_sp = sp;
		}

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			while (!stoppingToken.IsCancellationRequested)
			{
					using var scope = _sp.CreateScope();
					var db = scope.ServiceProvider.GetRequiredService<IMongoDatabase>();
				var store = scope.ServiceProvider.GetRequiredService<StoreCheckService>();
				var importer = scope.ServiceProvider.GetRequiredService<CardImportService>();
				var setBuilder = scope.ServiceProvider.GetRequiredService<SetCollectionService>();

				var games = db.GetCollection<Games>("games");
					var now = DateTime.UtcNow;

				var filter = Builders<Games>.Filter.And(
					Builders<Games>.Filter.Lte(g => g.FailCount, 10),
					Builders<Games>.Filter.Or(
						Builders<Games>.Filter.Eq(g => g.Status, "new"),

						Builders<Games>.Filter.And(
							Builders<Games>.Filter.In(g => g.Status, new[] { "store_throttled", "market_throttled" }),
							Builders<Games>.Filter.Lte(g => g.NextRetryAtUtc, now)
					),
						Builders<Games>.Filter.And(
							Builders<Games>.Filter.Eq(g => g.Status, "no_cards"),
							Builders<Games>.Filter.Lte(g => g.NextRetryAtUtc, now)
						),
						Builders<Games>.Filter.Eq(g => g.Status, "cards_possible")
					)
				);

				var batch = await games.Find(filter).SortBy(g => g.AppId).Limit(5).ToListAsync(stoppingToken);
					
					if (batch.Count == 0)
					{
						Console.WriteLine("No games to process.");
						await Task.Delay(IdleDelay, stoppingToken);
						continue;
					}

				foreach (var g in batch)
				{
					try
					{
						await games.UpdateOneAsync(
							x => x.AppId == g.AppId,
							Builders<Games>.Update
								.Set(g => g.Status, "processing")
								.Set(g => g.NextRetryAtUtc, null),
							cancellationToken: stoppingToken
						);

						if (g.Status == "new" || g.Status == "store_throttled" || g.Status == "no_cards")
						{
							bool hasCards;

							try
							{
								hasCards = await store.CheckAppIdExistsAsync(g.AppId, stoppingToken);
							}
							catch (Exception ex)
							{
								Console.WriteLine($"Error checking store for AppId {g.AppId}: {ex.Message}");

								await games.UpdateOneAsync(
									x => x.AppId == g.AppId,
									Builders<Games>.Update
										.Inc(g => g.FailCount, 1)
										.Set(g => g.Status, "store_throttled")
										.Set(g => g.NextRetryAtUtc, DateTime.UtcNow.Add(ThrottleRetryAfter)),
									cancellationToken: stoppingToken
								);

								await Task.Delay(Random.Shared.Next(1500, 2500), stoppingToken);
								continue;
							}

							if (!hasCards)
							{
								Console.WriteLine($"[INFO] AppId {g.AppId} does not have tradable cards.");

								await games.UpdateOneAsync(
								x => x.AppId == g.AppId,
								Builders<Games>.Update
									.Set(g => g.HasTradableCards, false)
									.Set(g => g.CardsImported, false)
									.Set(g => g.Status, "no_cards")
									.Set(g => g.CardImportedAtUtc, DateTime.UtcNow)
									.Set(g => g.NextRetryAtUtc, DateTime.UtcNow.Add(NoCardsRecheckAfter)),
								cancellationToken: stoppingToken
							);

								await Task.Delay(Random.Shared.Next(1500, 2500), stoppingToken);
								continue;
							}

							await games.UpdateOneAsync(
								x => x.AppId == g.AppId,
								Builders<Games>.Update
									.Set(g => g.HasTradableCards, true)
									.Set(g => g.CardsImported, true)
									.Set(g => g.Status, "cards_possible")
									.Set(g => g.CardImportedAtUtc, DateTime.UtcNow)
									.Set(x => x.NextRetryAtUtc, null),
								cancellationToken: stoppingToken
							);
						}

						ImportCardsResult importResult;

						try
						{
							importResult = await importer.ImportForGameAsync(g.AppId);
						}
						catch (Exception ex)
						{
							Console.WriteLine($"[MARKET] AppId {g.AppId} throttled: {ex.Message}");

							await games.UpdateOneAsync(
							x => x.AppId == g.AppId,
							Builders<Games>.Update
								.Inc(g => g.FailCount, 1)
								.Set(g => g.Status, "market_throttled")
								.Set(g => g.NextRetryAtUtc, DateTime.UtcNow.Add(MarketThrottleRetryAfter)),
							cancellationToken: stoppingToken
							);

							await Task.Delay(Random.Shared.Next(2000, 3500), stoppingToken);
							continue;
						}

						var normalImported = importResult.NormalImported;
						var foilImported = importResult.FoilImported;

						if (normalImported == 0 && foilImported == 0)
						{
							Console.WriteLine($"[INFO] No cards found for AppId {g.AppId} on market.");
							await games.UpdateOneAsync(
								x => x.AppId == g.AppId,
								Builders<Games>.Update
									.Set(g => g.Status, "cards_possible")
									.Set(g => g.NextRetryAtUtc, DateTime.UtcNow.AddHours(6)),
								cancellationToken: stoppingToken
							);

							await Task.Delay(Random.Shared.Next(800, 1400), stoppingToken);
							continue;
						}

						await games.UpdateOneAsync(
							x => x.AppId == g.AppId,
							Builders<Games>.Update
								.Set(g => g.HasTradableCards, true)
								.Set(g => g.CardsImported, true)
								.Set(g => g.Status, "ready")
								.Set(g => g.CardImportedAtUtc, DateTime.UtcNow)
								.Set(g => g.NextRetryAtUtc, null),
							cancellationToken: stoppingToken
						);

						await setBuilder.BuildSetAsync(g.AppId);
						await Task.Delay(Random.Shared.Next(800, 1400), stoppingToken);
					}
					catch (Exception ex)
					{
						Console.WriteLine($"Unexpected error processing AppId {g.AppId}: {ex.Message}");

						await games.UpdateOneAsync(
							x => x.AppId == g.AppId,
							Builders<Games>.Update
								.Inc(g => g.FailCount, 1)
								.Set(g => g.Status, "store_throttled")
								.Set(g => g.NextRetryAtUtc, DateTime.UtcNow.AddMinutes(30)),
							cancellationToken: stoppingToken
						);

						await Task.Delay(Random.Shared.Next(1500, 2500), stoppingToken);
					}
				}
			}	
		}
	}
}
