using MongoDB.Bson.Serialization.Attributes;

namespace SteamCards.Models
{
	[BsonIgnoreExtraElements]
	public class SetCollection
	{
		public int AppId { get; set; }
		public string GameName { get; set; } = null!;
		public bool IsFoil { get; set; }

		public int TotalCards { get; set; }
		public decimal TotalPrice { get; set; }
		public string Currency { get; set; } = "UAH";
		public string Url { get; set; } = null!;
		
		public List<SetItem> Items { get; set; } = new List<SetItem>();
	}

	public class SetItem
	{
		public string Name { get; set; } = null!;
		public string MarketHashName { get; set; } = null!;
		public decimal? Price { get; set; }
		public bool IsFoil { get; set; }
	}
}