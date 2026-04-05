using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace SteamCards.Models
{
	public class PriceCurrent
	{
		[BsonId]
		[BsonRepresentation(BsonType.ObjectId)]
		public string? Id { get; set; }
		public string MarketHashName { get; set; } = null!;
		public decimal? LowestPrice { get; set; }
		public string Currency { get; set; } = "UAH";
	    public DateTime UpdatedAtUtc { get; set; }
	}
}
