using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace SteamCards.Models
{
	[BsonIgnoreExtraElements]
	public class Cards
	{
		public int AppId { get; set; }
		public string GameName { get; set; } = null!;
		public string MarketHashName { get; set; } = null!;
		public string? CardName { get; set; }
		public decimal? Price { get; set; }
		public string? PriceText { get; set; }
		public string Currency { get; set; } = "UAH";
		public bool IsFoil { get; set; }
		public DateTime CreatedAtUtc { get; set; }
	}
}
