namespace SteamCards.Services
{
	public class SteamWorkController
	{
		private readonly SemaphoreSlim _steamWorkService = new(1, 1);

		private bool IsBusy => _steamWorkService.CurrentCount == 0;

		public async Task<IDisposable> EnterAsync(CancellationToken ct)
		{
			await _steamWorkService.WaitAsync(ct);
			return new Releaser(_steamWorkService);
		}

		private sealed class Releaser : IDisposable
		{
			private readonly SemaphoreSlim _semaphore;
			private bool _disposed;
			public Releaser(SemaphoreSlim semaphore)
			{
				_semaphore = semaphore;
			}
			public void Dispose()
			{
				if (_disposed) 
					return;

				_disposed = true;
				_semaphore.Release();
			}
		}
	}
}
