using MongoDB.Driver;
using SteamCards.Services;
using SteamCards.Models;

namespace SteamCards
{
	public class CatalogWorker : BackgroundService
	{
		private readonly IServiceProvider _sp;

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
				var games = db.GetCollection<Games>("games");

				var now = DateTime.UtcNow;
				var throttled = 0;

				var filter = Builders<Games>.Filter.And(
					Builders<Games>.Filter.Lte(g => g.FailCount, 10),
					Builders<Games>.Filter.Or(
						Builders<Games>.Filter.And(
							Builders<Games>.Filter.Eq(g => g.Status, "market_throttled"),
							Builders<Games>.Filter.Lte(g => g.NextRetryAtUtc, now)
						),
						Builders<Games>.Filter.Eq(g => g.Status, "cards_possible")
					)
				);

				var batch = await games.Find(filter).SortBy(g => g.AppId).Limit(2).ToListAsync(stoppingToken);

				if (batch.Count == 0)
				{
					Console.WriteLine("No games to process.");
					await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
					continue;
				}

				await Parallel.ForEachAsync(
					batch,
					new ParallelOptions
					{
						MaxDegreeOfParallelism = 2,
						CancellationToken = stoppingToken
					},
					async (g, ct) =>
					{
						if (Volatile.Read(ref throttled) == 1)
							return;

						using var itemScope = _sp.CreateScope();

						var importer = itemScope.ServiceProvider.GetRequiredService<CardImportService>();
						var setBuilder = itemScope.ServiceProvider.GetRequiredService<SetCollectionService>();
						var itemDb = itemScope.ServiceProvider.GetRequiredService<IMongoDatabase>();
						var itemGames = itemDb.GetCollection<Games>("games");

						var throttleDelay = g.FailCount switch
						{
							0 => TimeSpan.FromMinutes(3),
							1 => TimeSpan.FromMinutes(10),
							2 => TimeSpan.FromMinutes(30),
							_ => TimeSpan.FromHours(2)
						};

						try
						{
							await itemGames.UpdateOneAsync(
								x => x.AppId == g.AppId,
								Builders<Games>.Update
									.Set(g => g.Status, "processing")
									.Set(g => g.NextRetryAtUtc, null),
								cancellationToken: ct
							);

							ImportCardsResult importResult;

							try
							{
								importResult = await importer.ImportForGameAsync(g.AppId, cancellationToken: ct);
							}
							catch (SteamThrottledException ex)
							{
								Console.WriteLine($"[MARKET] AppId {g.AppId} throttled: {ex.Message}");

								await itemGames.UpdateOneAsync(
								x => x.AppId == g.AppId,
								Builders<Games>.Update
									.Inc(g => g.FailCount, 1)
									.Set(g => g.Status, "market_throttled")
									.Set(g => g.NextRetryAtUtc, DateTime.UtcNow.Add(throttleDelay)),
								cancellationToken: ct
								);

								Interlocked.Exchange(ref throttled, 1);
								return;
							}

							var normalImported = importResult.NormalImported;
							var foilImported = importResult.FoilImported;

							if (normalImported == 0 && foilImported == 0)
							{
								Console.WriteLine($"[INFO] No cards found for AppId {g.AppId} on market.");
								await itemGames.UpdateOneAsync(
									x => x.AppId == g.AppId,
									Builders<Games>.Update
										.Set(g => g.Status, "no_cards")
										.Set(g => g.NextRetryAtUtc, DateTime.UtcNow.AddHours(6)),
									cancellationToken: ct
								);

								await Task.Delay(Random.Shared.Next(1000, 1500), ct);
								return;
							}

							await itemGames.UpdateOneAsync(
								x => x.AppId == g.AppId,
								Builders<Games>.Update
									.Set(g => g.HasTradableCards, true)
									.Set(g => g.CardsImported, true)
									.Set(g => g.Status, "ready")
									.Set(g => g.CardImportedAtUtc, DateTime.UtcNow)
									.Set(g => g.NextRetryAtUtc, null),
								cancellationToken: ct
							);

							await setBuilder.BuildSetAsync(g.AppId);
							await Task.Delay(Random.Shared.Next(1000, 2000), ct);
						}
						catch (Exception ex)
						{
							Console.WriteLine($"Unexpected error processing AppId {g.AppId}: {ex.Message}");

							await itemGames.UpdateOneAsync(
								x => x.AppId == g.AppId,
								Builders<Games>.Update
									.Inc(g => g.FailCount, 1)
									.Set(g => g.Status, "market_throttled")
									.Set(g => g.NextRetryAtUtc, DateTime.UtcNow.AddMinutes(30)),
								cancellationToken: ct
							);

							await Task.Delay(Random.Shared.Next(3000, 5000), ct);
						}
					}
				);

				if (Volatile.Read(ref throttled) == 1)
				{
					await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);
				}
			}
		}
	}
}
