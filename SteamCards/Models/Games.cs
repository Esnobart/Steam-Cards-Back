using MongoDB.Bson.Serialization.Attributes;

namespace SteamCards.Models
{
	[BsonIgnoreExtraElements]
	public class Games
	{
		public int AppId { get; set; }
		public bool HasTradableCards { get; set; }
		public bool CardsImported { get; set; }
		public DateTime? CardImportedAtUtc { get; set; }
		public DateTime? PriceUpdatedAtUtc { get; set; }
		public string Status { get; set; } = "new";
		public int FailCount { get; set; }
		public DateTime? NextRetryAtUtc { get; set; }

	}
}
