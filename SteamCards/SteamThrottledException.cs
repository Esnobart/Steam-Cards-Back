namespace SteamCards
{
	public class SteamThrottledException : Exception
	{
		public int StatusCode { get; }

		public SteamThrottledException(int statusCode)
			: base($"Steam throttled requests with HTTP {statusCode}")
		{
			StatusCode = statusCode;
		}
	}
}
