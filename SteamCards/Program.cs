using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using SteamCards;
using SteamCards.Models;
using SteamCards.Services;

var builder = WebApplication.CreateBuilder(args);

// Controllers (как routes + controllers в express)
builder.Services.AddControllers();

// CORS (аналог cors())
builder.Services.AddCors(options =>
{
	options.AddDefaultPolicy(p => p
		.AllowAnyOrigin()
		.AllowAnyHeader()
		.AllowAnyMethod());
});

builder.Services.AddSingleton<IMongoDatabase>(sp =>
{
	var cs = builder.Configuration["Mongo:ConnectionString"];
	var mongoUrl = new MongoUrl(cs);
	var client = new MongoClient(mongoUrl);

	var dbName = mongoUrl.DatabaseName ?? "steam";
	return client.GetDatabase(dbName);
});

// Services 
builder.Services.AddHttpLogging();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient<SteamMarketService>(client =>
{
	client.DefaultRequestHeaders.UserAgent.ParseAdd(
		"Mozilla/5.0 (Windows NT 10.0; Win64; x64) SteamCardsApp/1.0"
	);
	client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient<StoreCheckService>(client =>
{
	client.DefaultRequestHeaders.UserAgent.ParseAdd(
		"Mozilla/5.0 (Windows NT 10.0; Win64; x64) SteamCardsApp/1.0"
	);
	client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient<CardImportService>(client =>
{
	client.DefaultRequestHeaders.UserAgent.ParseAdd(
		"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36");

	client.DefaultRequestHeaders.Accept.ParseAdd(
		"text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
	client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHostedService<CatalogWorker>();
builder.Services.AddScoped<CardsService>();
builder.Services.AddScoped<SetCollectionService>();


var app = builder.Build();

// Logger (аналог morgan)
app.UseHttpLogging();

// CORS
app.UseCors();

// JSON body parsing — в .NET оно уже встроено для Controllers
app.MapControllers();

app.MapGet("/", () => "SteamCards API running");

app.MapGet("/health", () => Results.Ok("OK"));

app.MapPost("/admin/games/seed-range", async (int from, int to, IMongoDatabase db) =>
{
	if (from <= 0 || to < from)
		return Results.BadRequest(new { message = "Invalid range" });

	var games = db.GetCollection<Games>("games");

	var models = new List<WriteModel<Games>>();
	for (int appId = from; appId <= to; appId++)
	{
		models.Add(new UpdateOneModel<Games>(
			Builders<Games>.Filter.Eq(x => x.AppId, appId),
			Builders<Games>.Update
			    .Set(x => x.AppId, appId)
				.Set(x => x.Status, "new")
				.Set(x => x.FailCount, 0)
				.Set(x => x.NextRetryAtUtc, null)
				.Set(x => x.CardImportedAtUtc, null)
				.Set(x => x.CardsImported, false)
				.Set(x => x.HasTradableCards, false)
		) { IsUpsert = true });

		if (models.Count >= 1000)
		{
			await games.BulkWriteAsync(models);
			models.Clear();
		}
	}

	if (models.Count > 0)
		await games.BulkWriteAsync(models);

	return Results.Ok(new { inserted = to - from + 1 });
});

app.MapPost("/admin/test/{appId:int}", async (int appId, CardImportService importer) =>
{
	var result = await importer.ImportForGameAsync(appId);
	return Results.Ok(result);
});

// 404 handler (аналог app.use((_, res) => ...))
// В ASP.NET Core обычно 404 возвращается автоматически,
// но можно сделать "красивый" JSON на fallback:
app.MapFallback(() => Results.Json(new { message = "Route not found" }, statusCode: 404));

// Global error handler (аналог твоего app.use((err, req, res, next) => ...))
app.UseExceptionHandler(errorApp =>
{
	errorApp.Run(async context =>
	{
		// Можно расширить, но оставим как у тебя: status + message
		context.Response.StatusCode = 500;
		context.Response.ContentType = "application/json";
		await context.Response.WriteAsJsonAsync(new { message = "Server error" });
	});
});

app.Run();
