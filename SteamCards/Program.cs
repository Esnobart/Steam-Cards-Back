using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using SteamCards;
using SteamCards.Models;
using SteamCards.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

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

builder.Services.AddHttpLogging();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient<SteamMarketService>(client =>
{
	client.DefaultRequestHeaders.UserAgent.ParseAdd(
		"Mozilla/5.0 (Windows NT 10.0; Win64; x64) SteamCardsApp/1.0"
	);
	client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient<CardGameDiscoveryService>(client =>
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

app.UseExceptionHandler(errorApp =>
{
	errorApp.Run(async context =>
	{
		context.Response.StatusCode = 500;
		context.Response.ContentType = "application/json";
		await context.Response.WriteAsJsonAsync(new { message = "Server error" });
	});
});

app.UseHttpLogging();

app.UseCors();

app.MapControllers();

app.MapGet("/", () => "SteamCards API running");

app.MapGet("/health", () => Results.Ok("OK"));

app.MapPost("/admin/games/discover-card-games", async (CardGameDiscoveryService discovery, IMongoDatabase db, CancellationToken ct) =>
{
	var discoveryResult = await discovery.DiscoveryService(ct);
	var appIds = discoveryResult.AppIds;
	var games = db.GetCollection<Games>("games");

	var writes = appIds.Select(appId =>
	    new UpdateOneModel<Games>(
			Builders<Games>.Filter.Eq(g => g.AppId, appId),
		    Builders<Games>.Update
			  .Set(g => g.AppId, appId)
			  .Set(g => g.HasTradableCards, true)
			  .SetOnInsert(g => g.CardsImported, false)
			  .SetOnInsert(g => g.Status, "cards_possible")
			  .SetOnInsert(g => g.FailCount, 0)
	    )
		{ IsUpsert = true }
	).ToList();

	if (writes.Count > 0)
		await games.BulkWriteAsync(writes, cancellationToken: ct);

	return Results.Ok(new {
		steamTotal = discoveryResult.SteamTotal,
		discovered = discoveryResult.Discovered,
		pages = discoveryResult.Pages,
	});
});

app.MapPost("/admin/cards/{appId:int}", async (int appId, bool? isFoil, CardImportService importer, SetCollectionService setBuilder, CancellationToken cancellationToken) =>
{
	var result = await importer.ImportForGameAsync(appId, isFoil, cancellationToken);
	await setBuilder.BuildSetAsync(appId);
	return Results.Ok(result);
});

app.MapPost("/admin/sets/{appId:int}", async (int appId, SetCollectionService setBuilder) =>
{
	var sets = await setBuilder.BuildSetAsync(appId);
	return Results.Ok(sets);
});


app.MapFallback(() => Results.Json(new { message = "Route not found" }, statusCode: 404));

app.Run();
