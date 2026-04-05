using MongoDB.Driver;
using SteamCards.Models;

namespace SteamCards.Services
{
	public class SetCollectionService
	{
		private readonly CardsService _cardsService;
		private readonly IMongoCollection<SetCollection> _sets;

		public SetCollectionService(CardsService cardsService, IMongoDatabase database)
		{
			_cardsService = cardsService;
			_sets = database.GetCollection<SetCollection>("sets");
		}

		public async Task<List<SetCollection>> BuildSetAsync(int appId)
		{
			var cards = await _cardsService.GetByGameAsync(appId);

			if (cards.Count == 0)
				return new List<SetCollection>();

			var result = new List<SetCollection>();

			var normalCards = cards.Where(c => !c.IsFoil).ToList();
			var foilCards = cards.Where(c => c.IsFoil).ToList();

			if (normalCards.Count > 0)
				result.Add(await BuildSet(appId, normalCards, false));

			if (foilCards.Count > 0)
				result.Add(await BuildSet(appId, foilCards, true));

			return result;
		}
		private async Task<SetCollection> BuildSet(int appId, List<Cards> cards, bool isFoil)
		{
			var items = cards.Select(c => new SetItem
			{
				Name = c.CardName,
				MarketHashName = c.MarketHashName,
				Price = c.Price,
				IsFoil = c.IsFoil,
			}).ToList();

            var total = items.Where(i => i.Price.HasValue).Sum(i => i.Price!.Value);

			var set = new SetCollection
			{
				AppId = appId,
				GameName = cards.First().GameName,
				IsFoil = isFoil,
				TotalCards = items.Count,
				TotalPrice = total,		
				Currency = "UAH",
				Url = $"https://steamcommunity.com/market/search?appid=753&category_753_Game%5B0%5D=tag_app_{appId}&category_753_item_class%5B0%5D=tag_item_class_2&l=english",
				Items = items.OrderByDescending(i => i.Price ?? 0m).ToList()
			};

			await _sets.ReplaceOneAsync(
				s => s.AppId == appId && s.IsFoil == isFoil,
				set,
				new ReplaceOptions { IsUpsert = true }
			);

			return set;
		}
	}
}
