using MongoDB.Driver;
using SteamCards.Models;

namespace SteamCards.Services
{
	public class CardsService
	{
		private readonly IMongoCollection<Cards> _cards;

		public CardsService(IMongoDatabase database)
		{
			_cards = database.GetCollection<Cards>("cards");
		}

		public Task<List<Cards>> GetByGameAsync(int AppId, bool? IsFoil = null)
		{
			if (IsFoil.HasValue)
				return _cards.Find(c => c.AppId == AppId && c.IsFoil == IsFoil.Value).ToListAsync();

			return _cards.Find(c => c.AppId == AppId).ToListAsync();
		}
			
		public Task CreateAsync(Cards newCard) =>
			_cards.InsertOneAsync(newCard);
	}
}
